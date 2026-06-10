using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class LiquidityDynamicsTests
{
    // Default flow windows {10, 30, 60, 300}: index 0 = 10s, 3 = 300s.
    private const int W10 = 0, W300 = 3;

    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private sealed class Harness
    {
        public readonly BookStateTracker Tracker = new();
        public readonly LiquidityDynamicsTracker Liquidity;
        private Timestamp _lastTs;

        public Harness(FeatureEngineOptions? options = null) =>
            Liquidity = new LiquidityDynamicsTracker(TickSize.Es, options ?? new FeatureEngineOptions());

        public void Book(ulong ts, (decimal?, uint, uint)[] bids, (decimal?, uint, uint)[] asks)
        {
            var e = TestEvents.BookChanged(BookCause.Modify, Side.None, 0.25m, 0, 0,
                TestEvents.Levels(bids, asks), ts: ts);
            Feed(e);
        }

        public void Trade(ulong ts, Side aggressor, decimal px, uint size,
            (decimal?, uint, uint)[] bids, (decimal?, uint, uint)[] asks)
        {
            var e = TestEvents.Trade(aggressor, px, size, TestEvents.Levels(bids, asks), ts);
            Feed(e);
        }

        private void Feed(MarketEvent e)
        {
            Tracker.Apply(in e);
            Liquidity.OnEvent(in e, Tracker);
            _lastTs = e.TsEvent;
        }

        public Timestamp Now => _lastTs;
    }

    private static (decimal?, uint, uint)[] L(params (decimal? Px, uint Sz)[] lv) =>
        lv.Select(x => (x.Px, x.Sz, 1u)).ToArray();

    // ----- F16 pull ratio: cancel vs add attribution -----

    [Fact]
    public void FirstBookState_IsBaseline_NoAddVolume()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));

        Assert.Equal(0, h.Liquidity.WindowSum(W300).BidAddVolume);
        Assert.Equal(0, h.Liquidity.WindowSum(W300).BidCancelVolume);
    }

    [Fact]
    public void DecreaseWithoutTrade_IsCancelled_IncreaseIsAdd()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Book(At(1), L((5000.00m, 4)), L((5000.25m, 10)));   // bid -6, no trade → cancel
        h.Book(At(2), L((5000.00m, 9)), L((5000.25m, 10)));   // bid +5 → add

        var agg = h.Liquidity.WindowSum(W300);
        Assert.Equal(6, agg.BidCancelVolume);
        Assert.Equal(5, agg.BidAddVolume);
        Assert.Equal(0, agg.AskCancelVolume);
        Assert.Equal(6.0 / 5.0, h.Liquidity.PullRatio(Side.Bid, W300)!.Value, precision: 10);
        Assert.Null(h.Liquidity.PullRatio(Side.Ask, W300)); // no ask adds → undefined
    }

    [Fact]
    public void DecreaseCoveredByTrade_IsTradedNotCancelled()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        // sell hits the bid for 4; the T record carries the post-trade book (bid 10→6)
        h.Trade(At(1), Side.Ask, 5000.00m, 4, L((5000.00m, 6)), L((5000.25m, 10)));

        Assert.Equal(0, h.Liquidity.WindowSum(W300).BidCancelVolume);
    }

    [Fact]
    public void DecreaseBeyondTradeBudget_SplitsTradedAndCancelled()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Trade(At(1), Side.Ask, 5000.00m, 4, L((5000.00m, 6)), L((5000.25m, 10))); // budget consumed by its own diff
        h.Book(At(1), L((5000.00m, 2)), L((5000.25m, 10)));   // further -4 in same second, budget empty → cancel

        Assert.Equal(4, h.Liquidity.WindowSum(W300).BidCancelVolume);
    }

    [Fact]
    public void CancelVolume_AgesOutOfShortWindow()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Book(At(1), L((5000.00m, 4)), L((5000.25m, 10)));
        h.Book(At(15), L((5000.00m, 4)), L((5000.25m, 10))); // 14s later

        Assert.Equal(0, h.Liquidity.WindowSum(W10).BidCancelVolume);
        Assert.Equal(6, h.Liquidity.WindowSum(W300).BidCancelVolume);
    }

    // ----- F17 replenish ratio / F18 refresh count -----

    [Fact]
    public void ReplenishRatio_TradedOverMaxDisplayed()
    {
        var h = new Harness();
        var p = Price.FromDecimal(5000.00m);
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Trade(At(1), Side.Ask, 5000.00m, 4, L((5000.00m, 6)), L((5000.25m, 10)));
        h.Trade(At(2), Side.Ask, 5000.00m, 6, L((5000.00m, 8)), L((5000.25m, 10))); // refreshed to 8 then hit

        // traded(P) = 10, max displayed(P) over window = 10
        Assert.Equal(1.0, h.Liquidity.ReplenishRatio(Side.Bid, p, h.Now)!.Value, precision: 10);
    }

    [Fact]
    public void RefreshCount_CountsIncreasesAtExistingPrice()
    {
        var h = new Harness();
        var p = Price.FromDecimal(5000.00m);
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Book(At(1), L((5000.00m, 4)), L((5000.25m, 10)));
        h.Book(At(2), L((5000.00m, 9)), L((5000.25m, 10)));   // refresh 1
        h.Book(At(3), L((5000.00m, 5)), L((5000.25m, 10)));
        h.Book(At(4), L((5000.00m, 12)), L((5000.25m, 10)));  // refresh 2

        Assert.Equal(2, h.Liquidity.RefreshCount(Side.Bid, p, h.Now));
        Assert.Equal(0, h.Liquidity.RefreshCount(Side.Ask, Price.FromDecimal(5000.25m), h.Now));
    }

    // ----- F21 depth change beyond best -----

    [Fact]
    public void DepthChangeBeyondBest_ComparesAgainstLaggedValue()
    {
        var h = new Harness();
        // beyond-best bid depth (levels 2-4) = 10+10+10 = 30
        h.Book(At(0),
            L((5000.00m, 5), (4999.75m, 10), (4999.50m, 10), (4999.25m, 10)),
            L((5000.25m, 5)));
        // 30s later it has grown to 45
        h.Book(At(30),
            L((5000.00m, 5), (4999.75m, 20), (4999.50m, 15), (4999.25m, 10)),
            L((5000.25m, 5)));

        Assert.Equal(0.5, h.Liquidity.DepthChangeBeyondBest(Side.Bid)!.Value, precision: 10);
        Assert.Null(h.Liquidity.DepthChangeBeyondBest(Side.Ask)); // no beyond-best ask depth → undefined
    }

    // ----- F22 vanish flag -----

    [Fact]
    public void VanishFlag_SetWhenDisplayedDropsUntradedBeyondRatio()
    {
        var h = new Harness();
        var p = Price.FromDecimal(5000.00m);
        h.Book(At(0), L((5000.00m, 100)), L((5000.25m, 10)));
        h.Book(At(1), L((5000.00m, 5)), L((5000.25m, 10))); // -95 untraded = 95% of max

        Assert.True(h.Liquidity.VanishFlag(Side.Bid, p, 5, h.Now));
    }

    [Fact]
    public void VanishFlag_NotSetWhenDropWasTraded()
    {
        var h = new Harness();
        var p = Price.FromDecimal(5000.00m);
        h.Book(At(0), L((5000.00m, 100)), L((5000.25m, 10)));
        h.Trade(At(1), Side.Ask, 5000.00m, 95, L((5000.00m, 5)), L((5000.25m, 10)));

        Assert.False(h.Liquidity.VanishFlag(Side.Bid, p, 5, h.Now));
    }
}
