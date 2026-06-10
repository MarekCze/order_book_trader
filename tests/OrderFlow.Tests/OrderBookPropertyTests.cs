using OrderFlow.Backtest;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>
/// Property-style sanity checks over a seeded synthetic random-walk MBO sequence
/// (deterministic — same seed, same events, every run).
/// </summary>
public class OrderBookPropertyTests
{
    [Theory]
    [InlineData(42, 30_000)]
    [InlineData(7, 30_000)]
    [InlineData(1234, 30_000)]
    public void RandomWalk_BookStaysUncrossed_AndAggregatesMatchOrders(int seed, long count)
    {
        var book = new OrderBook();
        long applied = 0;
        foreach (var e in new SyntheticMboGenerator(seed).Generate(count))
        {
            book.Apply(in e);
            applied++;

            // Invariant 1: best bid strictly below best ask whenever both sides exist.
            if (book.TryGetBest(Side.Bid, out var bb) && book.TryGetBest(Side.Ask, out var ba))
            {
                Assert.True(bb.Price < ba.Price,
                    $"Crossed book after event {applied}: bid {bb.Price} >= ask {ba.Price}");
            }

            // Invariant 2 (sampled — O(n)): displayed totals equal the sum of tracked orders.
            if (applied % 1_000 == 0)
            {
                AssertAggregatesMatch(book);
            }
        }
        Assert.Equal(count, applied);
        AssertAggregatesMatch(book);
        Assert.Equal(0, book.Anomalies.Total);
    }

    private static void AssertAggregatesMatch(OrderBook book)
    {
        var (bidTotal, askTotal, orderCount) = book.RecomputeTotalsForVerification();
        Assert.Equal(bidTotal, book.DisplayedTotal(Side.Bid));
        Assert.Equal(askTotal, book.DisplayedTotal(Side.Ask));
        Assert.Equal(orderCount, book.OrderCount);
        Assert.True(bidTotal >= 0 && askTotal >= 0);
    }

    [Fact]
    public void Generator_IsDeterministicForSameSeed()
    {
        var a = new SyntheticMboGenerator(99).Generate(5_000).ToArray();
        var b = new SyntheticMboGenerator(99).Generate(5_000).ToArray();
        Assert.Equal(a, b);
    }
}
