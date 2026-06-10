using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using static OrderFlow.Tests.TestEvents;

namespace OrderFlow.Tests;

/// <summary>
/// Golden tests: small hand-crafted MBP-10 event sequences with fully asserted resulting
/// book states, per the CLAUDE.md engineering standard for the book state tracker.
/// </summary>
public class BookStateTrackerGoldenTests
{
    private static BookStateTracker Build(params MarketEvent[] events)
    {
        var tracker = new BookStateTracker();
        foreach (var e in events)
        {
            tracker.Apply(in e);
        }
        return tracker;
    }

    private static PriceLevel[] Top(BookStateTracker tracker, Side side, int k)
    {
        var buf = new PriceLevel[k];
        int n = tracker.CopyTopLevels(side, buf);
        return buf[..n];
    }

    [Fact]
    public void Golden1_BookChanged_RetainsCarriedState_WithCorrectBbo()
    {
        var levels = Levels(
            bids: [(4999.75m, 15, 2), (4999.50m, 7, 1)],
            asks: [(5000.00m, 3, 1), (5000.25m, 10, 2)]);
        var tracker = Build(BookChanged(BookCause.Add, Side.Bid, 4999.75m, 5, 0, levels));

        Assert.True(tracker.HasState);
        Assert.Equal(levels, tracker.Levels);
        Assert.True(tracker.TryGetBest(Side.Bid, out var bb));
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.75m), 15, 2), bb);
        Assert.True(tracker.TryGetBest(Side.Ask, out var ba));
        Assert.Equal(new PriceLevel(Price.FromDecimal(5000.00m), 3, 1), ba);
        Assert.True(tracker.TryGetSpreadTicks(TickSize.Es, out long spread));
        Assert.Equal(1, spread);
        Assert.Equal(22, tracker.DisplayedTotal(Side.Bid));
        Assert.Equal(13, tracker.DisplayedTotal(Side.Ask));
        Assert.Equal(2, tracker.LevelCount(Side.Bid));
        Assert.Equal(2, tracker.LevelCount(Side.Ask));
    }

    [Fact]
    public void Golden2_SuccessiveUpdates_StateAlwaysEqualsMostRecentRecord()
    {
        var state1 = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 4, 1)]);
        var state2 = Levels(bids: [(4999.75m, 25, 3)], asks: [(5000.00m, 4, 1)]);
        var state3 = Levels(bids: [(4999.50m, 6, 1)], asks: [(5000.00m, 4, 1)]);

        var tracker = new BookStateTracker();
        tracker.Apply(BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, state1));
        Assert.Equal(state1, tracker.Levels);

        tracker.Apply(BookChanged(BookCause.Add, Side.Bid, 4999.75m, 15, 0, state2));
        Assert.Equal(state2, tracker.Levels);

        // Cancel that wipes the old best: the record carries the new state wholesale.
        tracker.Apply(BookChanged(BookCause.Cancel, Side.Bid, 4999.75m, 25, 0, state3));
        Assert.Equal(state3, tracker.Levels);
        Assert.True(tracker.TryGetBest(Side.Bid, out var bb));
        Assert.Equal(Price.FromDecimal(4999.50m), bb.Price);
    }

    [Fact]
    public void Golden3_TradeRecord_UpdatesStateAndStaysConsistent()
    {
        var before = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 8, 2), (5000.25m, 5, 1)]);
        var afterTrade = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 3, 1), (5000.25m, 5, 1)]);

        var tracker = Build(
            BookChanged(BookCause.Add, Side.Ask, 5000.00m, 8, 0, before),
            Trade(Side.Bid, 5000.00m, 5, afterTrade)); // buyer lifts 5 lots from the offer

        Assert.Equal(afterTrade, tracker.Levels);
        Assert.True(tracker.TryGetBest(Side.Ask, out var ba));
        Assert.Equal(new PriceLevel(Price.FromDecimal(5000.00m), 3, 1), ba);
        // Trade left a coherent two-sided book.
        Assert.True(tracker.TryGetSpreadTicks(TickSize.Es, out long spread));
        Assert.Equal(1, spread);
    }

    [Fact]
    public void Golden4_TradeConsumingBestLevel_RevealsDeeperLevel()
    {
        var before = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 5, 1), (5000.25m, 12, 3)]);
        // The 5-lot offer is fully consumed; the second level becomes the new best ask.
        var after = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.25m, 12, 3)]);

        var tracker = Build(
            BookChanged(BookCause.Add, Side.Ask, 5000.00m, 5, 0, before),
            Trade(Side.Bid, 5000.00m, 5, after));

        Assert.True(tracker.TryGetBest(Side.Ask, out var ba));
        Assert.Equal(new PriceLevel(Price.FromDecimal(5000.25m), 12, 3), ba);
        Assert.True(tracker.TryGetSpreadTicks(TickSize.Es, out long spread));
        Assert.Equal(2, spread); // spread widened from 1 to 2 ticks
    }

    [Fact]
    public void Golden5_BookClear_EmptiesState_AndBookRebuilds()
    {
        var state = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 4, 1)]);
        var tracker = Build(
            BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, state),
            Clear());

        Assert.False(tracker.HasState);
        Assert.Equal(BookLevels.CreateEmpty(), tracker.Levels);
        Assert.False(tracker.TryGetBest(Side.Bid, out _));
        Assert.False(tracker.TryGetBest(Side.Ask, out _));
        Assert.False(tracker.TryGetSpreadTicks(TickSize.Es, out _));
        Assert.Equal(0, tracker.DisplayedTotal(Side.Bid));
        Assert.Equal(0, tracker.LevelCount(Side.Ask));

        var rebuilt = Levels(bids: [(4998.00m, 3, 1)], asks: []);
        tracker.Apply(BookChanged(BookCause.Add, Side.Bid, 4998.00m, 3, 0, rebuilt));
        Assert.True(tracker.HasState);
        Assert.True(tracker.TryGetBest(Side.Bid, out var bb));
        Assert.Equal(new PriceLevel(Price.FromDecimal(4998.00m), 3, 1), bb);
    }

    [Fact]
    public void Golden6_OneSidedAndEmptyBooks_QueriesDegradeGracefully()
    {
        var bidOnly = Levels(bids: [(4999.75m, 10, 1), (4999.50m, 4, 2)], asks: []);
        var tracker = Build(BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, bidOnly));

        Assert.True(tracker.TryGetBest(Side.Bid, out _));
        Assert.False(tracker.TryGetBest(Side.Ask, out _));
        Assert.False(tracker.TryGetSpreadTicks(TickSize.Es, out _));
        Assert.Equal(14, tracker.DisplayedTotal(Side.Bid));
        Assert.Equal(0, tracker.DisplayedTotal(Side.Ask));
        Assert.Equal(2, tracker.LevelCount(Side.Bid));
        Assert.Equal(0, tracker.LevelCount(Side.Ask));
        Assert.Empty(Top(tracker, Side.Ask, 10));
    }

    [Fact]
    public void Golden7_FullDepth_CopyTopLevels_PreservesOrderAndContents()
    {
        var bids = new (decimal?, uint, uint)[BookLevels.Depth];
        var asks = new (decimal?, uint, uint)[BookLevels.Depth];
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            bids[i] = (4999.75m - i * 0.25m, (uint)(10 + i), (uint)(1 + i));
            asks[i] = (5000.00m + i * 0.25m, (uint)(20 + i), (uint)(2 + i));
        }
        var tracker = Build(BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, Levels(bids, asks)));

        var topBids = Top(tracker, Side.Bid, 10);
        var topAsks = Top(tracker, Side.Ask, 10);
        Assert.Equal(10, topBids.Length);
        Assert.Equal(10, topAsks.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(new PriceLevel(Price.FromDecimal(4999.75m - i * 0.25m), 10 + i, 1 + i), topBids[i]);
            Assert.Equal(new PriceLevel(Price.FromDecimal(5000.00m + i * 0.25m), 20 + i, 2 + i), topAsks[i]);
        }
        // Strict price ordering: bids descending, asks ascending.
        for (int i = 1; i < 10; i++)
        {
            Assert.True(topBids[i].Price < topBids[i - 1].Price);
            Assert.True(topAsks[i].Price > topAsks[i - 1].Price);
        }
        Assert.Equal(Price.FromDecimal(4999.25m), topBids[2].Price);
        Assert.Equal(12, tracker.DisplayedAt(Side.Bid, Price.FromDecimal(4999.25m)));
        Assert.Equal(0, tracker.DisplayedAt(Side.Bid, Price.FromDecimal(4990.00m)));
    }

    [Fact]
    public void Golden8_SnapshotRun_ThenIncrementalUpdates()
    {
        var snapState = Levels(bids: [(4999.75m, 10, 1)], asks: [(5000.00m, 4, 1)]);
        var liveState = Levels(bids: [(4999.75m, 10, 1), (4999.50m, 5, 1)], asks: [(5000.00m, 4, 1)]);

        var tracker = Build(
            BookChanged(BookCause.Add, Side.Bid, 4999.75m, 10, 0, snapState, MarketEventFlags.Snapshot),
            new MarketEvent(MarketEventKind.SnapshotEnd, BookCause.None, Instrument,
                new Timestamp(2_000), new Timestamp(2_001), 99,
                Price.Undefined, 0, Side.None, MarketEventFlags.None, 0, default),
            BookChanged(BookCause.Add, Side.Bid, 4999.50m, 5, 1, liveState));

        // SnapshotEnd is a pure boundary marker — it must not disturb retained state.
        Assert.Equal(liveState, tracker.Levels);
        Assert.Equal(2, tracker.LevelCount(Side.Bid));
        Assert.True(tracker.TryGetBest(Side.Bid, out var bb));
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.75m), 10, 1), bb);
    }

    [Fact]
    public void Golden9_SnapshotEndAlone_DoesNotCreateState()
    {
        var tracker = Build(
            new MarketEvent(MarketEventKind.SnapshotEnd, BookCause.None, Instrument,
                new Timestamp(1_000), new Timestamp(1_001), 1,
                Price.Undefined, 0, Side.None, MarketEventFlags.None, 0, default));

        Assert.False(tracker.HasState);
        Assert.False(tracker.TryGetBest(Side.Bid, out _));
    }
}
