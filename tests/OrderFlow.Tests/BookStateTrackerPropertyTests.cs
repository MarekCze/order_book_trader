using OrderFlow.Backtest;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>
/// Property-style sanity checks over a seeded synthetic random-walk MBP-10 sequence
/// (deterministic — same seed, same events, every run).
/// </summary>
public class BookStateTrackerPropertyTests
{
    [Theory]
    [InlineData(42, 30_000)]
    [InlineData(7, 30_000)]
    [InlineData(1234, 30_000)]
    public void RandomWalk_BookStaysUncrossed_AndStateEqualsMostRecentRecord(int seed, long count)
    {
        var tracker = new BookStateTracker();
        long applied = 0;
        foreach (var e in new SyntheticMbp10Generator(seed).Generate(count))
        {
            tracker.Apply(in e);
            applied++;

            // Invariant 1: the published state is byte-for-byte the level array of the
            // most recent record (the MBP-10 replacement for the old sum-of-orders check).
            Assert.Equal(e.Levels, tracker.Levels);

            // Invariant 2: best bid strictly below best ask whenever both sides exist.
            if (tracker.TryGetBest(Side.Bid, out var bb) && tracker.TryGetBest(Side.Ask, out var ba))
            {
                Assert.True(bb.Price < ba.Price,
                    $"Crossed book after event {applied}: bid {bb.Price} >= ask {ba.Price}");
            }

            // Invariant 3: per-side prices strictly ordered and populated levels contiguous.
            if (applied % 1_000 == 0)
            {
                AssertWellFormed(tracker);
            }
        }
        Assert.Equal(count, applied);
        AssertWellFormed(tracker);
    }

    private static void AssertWellFormed(BookStateTracker tracker)
    {
        Span<PriceLevel> levels = stackalloc PriceLevel[BookLevels.Depth];
        int nBids = tracker.CopyTopLevels(Side.Bid, levels);
        for (int i = 1; i < nBids; i++)
        {
            Assert.True(levels[i].Price < levels[i - 1].Price, "Bid prices not strictly descending");
        }
        Assert.Equal(nBids, tracker.LevelCount(Side.Bid));

        int nAsks = tracker.CopyTopLevels(Side.Ask, levels);
        for (int i = 1; i < nAsks; i++)
        {
            Assert.True(levels[i].Price > levels[i - 1].Price, "Ask prices not strictly ascending");
        }
        Assert.Equal(nAsks, tracker.LevelCount(Side.Ask));
    }

    [Fact]
    public void Generator_IsDeterministicForSameSeed()
    {
        var a = new SyntheticMbp10Generator(99).Generate(5_000).ToArray();
        var b = new SyntheticMbp10Generator(99).Generate(5_000).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Generator_TradesCarryAggressorSide_AndPositiveSize()
    {
        var events = new SyntheticMbp10Generator(3).Generate(20_000).ToArray();
        var trades = events.Where(e => e.Kind == MarketEventKind.Trade).ToArray();
        Assert.NotEmpty(trades);
        Assert.All(trades, t =>
        {
            Assert.NotEqual(Side.None, t.Side);
            Assert.True(t.Size > 0);
        });
        // Both aggressor directions occur in a long enough walk.
        Assert.Contains(trades, t => t.Side == Side.Bid);
        Assert.Contains(trades, t => t.Side == Side.Ask);
    }
}
