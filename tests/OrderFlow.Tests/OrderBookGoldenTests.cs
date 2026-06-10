using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using static OrderFlow.Tests.TestEvents;

namespace OrderFlow.Tests;

/// <summary>
/// Golden tests: small hand-crafted event sequences with fully asserted resulting book
/// states, per the CLAUDE.md engineering standard for the book builder.
/// </summary>
public class OrderBookGoldenTests
{
    private static OrderBook Build(params MarketEvent[] events)
    {
        var book = new OrderBook();
        foreach (var e in events)
        {
            book.Apply(in e);
        }
        return book;
    }

    private static PriceLevel[] Top(OrderBook book, Side side, int k)
    {
        var buf = new PriceLevel[k];
        int n = book.CopyTopLevels(side, buf);
        return buf[..n];
    }

    [Fact]
    public void Golden1_AddsAggregateIntoLevels_WithCorrectBbo()
    {
        var book = Build(
            Add(1, Side.Bid, 4999.75m, 10),
            Add(2, Side.Bid, 4999.75m, 5),
            Add(3, Side.Bid, 4999.50m, 7),
            Add(4, Side.Ask, 5000.00m, 3),
            Add(5, Side.Ask, 5000.25m, 9),
            Add(6, Side.Ask, 5000.25m, 1));

        Assert.Equal(6, book.OrderCount);
        Assert.Equal(22, book.DisplayedTotal(Side.Bid));
        Assert.Equal(13, book.DisplayedTotal(Side.Ask));

        Assert.Equal(new[]
        {
            new PriceLevel(Price.FromDecimal(4999.75m), 15, 2),
            new PriceLevel(Price.FromDecimal(4999.50m), 7, 1),
        }, Top(book, Side.Bid, 10));
        Assert.Equal(new[]
        {
            new PriceLevel(Price.FromDecimal(5000.00m), 3, 1),
            new PriceLevel(Price.FromDecimal(5000.25m), 10, 2),
        }, Top(book, Side.Ask, 10));

        Assert.True(book.TryGetSpreadTicks(TickSize.Es, out long spread));
        Assert.Equal(1, spread);
        Assert.Equal(0, book.Anomalies.Total);
    }

    [Fact]
    public void Golden2_PartialThenFullCancel_RemovesOrderAndLevel()
    {
        var book = Build(
            Add(1, Side.Bid, 4999.75m, 10),
            Cancel(1, Side.Bid, 4999.75m, 4));

        Assert.Equal(1, book.OrderCount);
        Assert.Equal(6, book.DisplayedAt(Side.Bid, Price.FromDecimal(4999.75m)));
        Assert.True(book.TryGetBest(Side.Bid, out var best));
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.75m), 6, 1), best);

        book.Apply(Cancel(1, Side.Bid, 4999.75m, 6));
        Assert.Equal(0, book.OrderCount);
        Assert.Equal(0, book.DisplayedTotal(Side.Bid));
        Assert.Equal(0, book.LevelCount(Side.Bid));
        Assert.False(book.TryGetBest(Side.Bid, out _));
        Assert.Equal(0, book.Anomalies.Total);
    }

    [Fact]
    public void Golden3_ModifyMovesPrice_AndChangesSize()
    {
        var book = Build(
            Add(1, Side.Bid, 4999.75m, 10),
            Add(2, Side.Bid, 4999.75m, 8),
            Modify(1, Side.Bid, 4999.50m, 10)); // price move relocates the order

        Assert.Equal(new[]
        {
            new PriceLevel(Price.FromDecimal(4999.75m), 8, 1),
            new PriceLevel(Price.FromDecimal(4999.50m), 10, 1),
        }, Top(book, Side.Bid, 10));

        book.Apply(Modify(1, Side.Bid, 4999.50m, 25)); // size-only change in place
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.50m), 25, 1), Top(book, Side.Bid, 10)[1]);
        Assert.Equal(33, book.DisplayedTotal(Side.Bid));
        Assert.Equal(0, book.Anomalies.Total);
    }

    [Fact]
    public void Golden4_FillReducesThenRemoves_TradeNeverMutates()
    {
        var book = Build(
            Add(1, Side.Ask, 5000.00m, 10),
            Fill(1, Side.Ask, 5000.00m, 4));

        Assert.Equal(6, book.DisplayedAt(Side.Ask, Price.FromDecimal(5000.00m)));

        book.Apply(Trade(Side.Bid, 5000.00m, 4)); // aggressor summary: no book mutation
        Assert.Equal(6, book.DisplayedAt(Side.Ask, Price.FromDecimal(5000.00m)));
        Assert.Equal(1, book.OrderCount);

        book.Apply(Fill(1, Side.Ask, 5000.00m, 6));
        Assert.Equal(0, book.OrderCount);
        Assert.Equal(0, book.LevelCount(Side.Ask));
        Assert.Equal(0, book.Anomalies.Total);
    }

    [Fact]
    public void Golden5_SnapshotReplay_ThenIncrementalUpdates()
    {
        var book = Build(
            // Snapshot-flagged records at stream start rebuild the prior book state.
            Add(1, Side.Bid, 4999.75m, 10, MarketEventFlags.Snapshot),
            Add(2, Side.Bid, 4999.50m, 6, MarketEventFlags.Snapshot),
            Add(3, Side.Ask, 5000.00m, 4, MarketEventFlags.Snapshot),
            Add(4, Side.Ask, 5000.25m, 12, MarketEventFlags.Snapshot),
            new MarketEvent(MarketEventKind.SnapshotEnd, Instrument, new Timestamp(2_000), new Timestamp(2_001), 99,
                0, Price.Undefined, 0, Side.None, MarketEventFlags.None));

        Assert.Equal(4, book.OrderCount);
        Assert.Equal(16, book.DisplayedTotal(Side.Bid));
        Assert.Equal(16, book.DisplayedTotal(Side.Ask));

        // Incremental updates continue seamlessly against snapshot-created orders.
        book.Apply(Cancel(2, Side.Bid, 4999.50m, 6));
        book.Apply(Add(5, Side.Bid, 4999.75m, 5));
        Assert.Equal(new[]
        {
            new PriceLevel(Price.FromDecimal(4999.75m), 15, 2),
        }, Top(book, Side.Bid, 10));
        Assert.Equal(0, book.Anomalies.Total);
    }

    [Fact]
    public void Golden6_BookClear_EmptiesEverything_AndBookRebuilds()
    {
        var book = Build(
            Add(1, Side.Bid, 4999.75m, 10),
            Add(2, Side.Ask, 5000.00m, 4),
            Clear());

        Assert.Equal(0, book.OrderCount);
        Assert.Equal(0, book.DisplayedTotal(Side.Bid));
        Assert.Equal(0, book.DisplayedTotal(Side.Ask));
        Assert.False(book.TryGetBest(Side.Bid, out _));
        Assert.False(book.TryGetBest(Side.Ask, out _));

        book.Apply(Add(10, Side.Bid, 4998.00m, 3));
        Assert.True(book.TryGetBest(Side.Bid, out var best));
        Assert.Equal(new PriceLevel(Price.FromDecimal(4998.00m), 3, 1), best);
    }

    [Fact]
    public void Golden7_EventsForUnknownOrders_AreCountedNotApplied()
    {
        var book = Build(Add(1, Side.Bid, 4999.75m, 10));

        book.Apply(Cancel(99, Side.Bid, 4999.75m, 5));
        book.Apply(Fill(98, Side.Ask, 5000.00m, 5));
        Assert.Equal(1, book.OrderCount);
        Assert.Equal(10, book.DisplayedTotal(Side.Bid));
        Assert.Equal(1, book.Anomalies.CancelUnknownOrder);
        Assert.Equal(1, book.Anomalies.FillUnknownOrder);

        // Modify of an unknown order upserts (mid-session stream start) and is counted.
        book.Apply(Modify(97, Side.Ask, 5000.25m, 7));
        Assert.Equal(2, book.OrderCount);
        Assert.Equal(7, book.DisplayedAt(Side.Ask, Price.FromDecimal(5000.25m)));
        Assert.Equal(1, book.Anomalies.ModifyUnknownOrder);
    }

    [Fact]
    public void Golden8_DeepInterleaving_KeepsMbp10Ordered()
    {
        var book = new OrderBook();
        // 12 bid levels, two orders each.
        for (int i = 0; i < 12; i++)
        {
            decimal px = 5000.00m - i * 0.25m;
            book.Apply(Add((ulong)(2 * i + 1), Side.Bid, px, (uint)(10 + i)));
            book.Apply(Add((ulong)(2 * i + 2), Side.Bid, px, 5));
        }
        // Knock out the best level entirely, shrink the second, relocate one order upward.
        book.Apply(Cancel(1, Side.Bid, 5000.00m, 10));
        book.Apply(Cancel(2, Side.Bid, 5000.00m, 5));
        book.Apply(Fill(3, Side.Bid, 4999.75m, 11)); // full fill of the 11-lot
        book.Apply(Modify(24, Side.Bid, 5000.25m, 5)); // from 4997.25 to a new best

        var top = Top(book, Side.Bid, 10);
        Assert.Equal(10, top.Length);
        Assert.Equal(new PriceLevel(Price.FromDecimal(5000.25m), 5, 1), top[0]);
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.75m), 5, 1), top[1]);
        Assert.Equal(new PriceLevel(Price.FromDecimal(4999.50m), 17, 2), top[2]);
        // Strictly descending prices all the way down.
        for (int i = 1; i < top.Length; i++)
        {
            Assert.True(top[i].Price < top[i - 1].Price);
        }
        // Incremental aggregates equal a full recomputation from per-order state.
        var (bidTotal, askTotal, orderCount) = book.RecomputeTotalsForVerification();
        Assert.Equal(bidTotal, book.DisplayedTotal(Side.Bid));
        Assert.Equal(askTotal, book.DisplayedTotal(Side.Ask));
        Assert.Equal(orderCount, book.OrderCount);
    }
}
