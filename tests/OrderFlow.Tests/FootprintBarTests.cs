using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class VolumeBarBuilderTests
{
    private static readonly TickSize Tick = TickSize.Es;

    private static VolumeBarBuilder Builder(int barSize = 10) =>
        new(Tick, new FeatureEngineOptions { BarVolumeSize = barSize });

    private static Price Px(decimal p) => Price.FromDecimal(p);

    [Fact]
    public void Bar_CompletesWhenVolumeReachesThreshold()
    {
        var b = Builder(barSize: 10);
        Assert.Null(b.OnTrade(Px(5000.00m), 4, Side.Bid));
        Assert.Null(b.OnTrade(Px(5000.25m), 5, Side.Ask));
        var bar = b.OnTrade(Px(5000.25m), 3, Side.Bid); // 12 ≥ 10 → closes

        Assert.NotNull(bar);
        Assert.Equal(12, bar!.Volume);
        Assert.Equal(0, b.Forming.Volume); // fresh forming bar
        Assert.Equal(1, b.CompletedCount);
    }

    [Fact]
    public void Bar_TracksOhlcAndDelta()
    {
        var b = Builder(barSize: 10);
        b.OnTrade(Px(5000.00m), 2, Side.Bid);   // open
        b.OnTrade(Px(5000.50m), 3, Side.Bid);   // high
        b.OnTrade(Px(4999.75m), 1, Side.Ask);   // low
        var bar = b.OnTrade(Px(5000.25m), 4, Side.Ask)!; // close, total 10

        Assert.Equal(Px(5000.00m), bar.Open);
        Assert.Equal(Px(5000.25m), bar.Close);
        Assert.Equal(Px(5000.50m), bar.High);
        Assert.Equal(Px(4999.75m), bar.Low);
        Assert.Equal(5, bar.BuyVolume);
        Assert.Equal(5, bar.SellVolume);
        Assert.Equal(0, bar.Delta);
    }

    [Fact]
    public void Poc_IsMaxVolumePrice_TieGoesToLowerPrice()
    {
        var b = Builder(barSize: 100);
        b.OnTrade(Px(5000.00m), 5, Side.Bid);
        b.OnTrade(Px(5000.25m), 5, Side.Ask);
        b.OnTrade(Px(5000.50m), 3, Side.Bid);

        Assert.Equal(Px(5000.00m), b.Forming.Poc(Tick));
    }
}

public class FootprintFeatureTests
{
    private static readonly TickSize Tick = TickSize.Es;
    private static readonly FeatureEngineOptions Opts = new() { BarVolumeSize = 1000 };

    private static Price Px(decimal p) => Price.FromDecimal(p);

    private static FootprintBar Bar(params (decimal Px, long Buy, long Sell)[] rows)
    {
        var builder = new VolumeBarBuilder(Tick, Opts);
        foreach (var (px, buy, sell) in rows)
        {
            if (buy > 0)
            {
                builder.OnTrade(Px(px), (uint)buy, Side.Bid);
            }
            if (sell > 0)
            {
                builder.OnTrade(Px(px), (uint)sell, Side.Ask);
            }
        }
        return builder.Forming;
    }

    [Fact]
    public void DiagonalImbalances_BuyComparesAgainstSellOneTickBelow()
    {
        // buy(5000.25)=30 vs sell(5000.00)=5 → 6:1 ≥ 3:1 → one buy imbalance
        // sell(5000.00)=5 vs buy(5000.25)=30 → no sell imbalance
        // sell(5000.25)=2 vs empty above (floored 1) → 2 < 3 → no edge imbalance
        var bar = Bar((5000.00m, 2, 5), (5000.25m, 30, 2));

        var (buyCount, sellCount) = FootprintFeatures.DiagonalImbalanceCounts(bar, Tick, ratio: 3.0);
        Assert.Equal(1, buyCount);
        Assert.Equal(0, sellCount);
    }

    [Fact]
    public void DiagonalImbalances_MissingOpposingLevelFloorsAtOne()
    {
        // buy(5000.25)=4 with no sell below → 4 ≥ 3×1 → imbalance
        var bar = Bar((5000.25m, 4, 0));

        var (buyCount, _) = FootprintFeatures.DiagonalImbalanceCounts(bar, Tick, ratio: 3.0);
        Assert.Equal(1, buyCount);
    }

    [Fact]
    public void StackedImbalanceLen_LongestAdjacentSameSideRun()
    {
        // three consecutive buy-imbalanced prices, one gap, then one more
        var bar = Bar(
            (5000.00m, 0, 1),
            (5000.25m, 30, 1),  // buy imb (vs sell@5000.00 = 1)
            (5000.50m, 30, 1),  // buy imb
            (5000.75m, 30, 50), // buy imb (30 ≥ 3×1? vs sell@5000.50=1 → yes)
            (5001.00m, 1, 1));  // not imbalanced (1 < 3×50? vs sell@5000.75=50 → no)

        Assert.Equal(3, FootprintFeatures.StackedImbalanceLen(bar, Tick, ratio: 3.0));
    }

    [Fact]
    public void ExtremeVolumeShare_TopAndBottomTwoTicks()
    {
        var bar = Bar(
            (5000.00m, 10, 0),  // bottom tick
            (5000.25m, 10, 0),  // bottom 2nd tick
            (5000.50m, 20, 0),
            (5000.75m, 30, 0),  // top 2nd tick
            (5001.00m, 30, 0)); // top tick

        Assert.Equal(60.0 / 100.0, FootprintFeatures.ExtremeVolumeShare(bar, Tick, top: true)!.Value, precision: 10);
        Assert.Equal(20.0 / 100.0, FootprintFeatures.ExtremeVolumeShare(bar, Tick, top: false)!.Value, precision: 10);
    }

    [Fact]
    public void UnfinishedAuction_TwoSidedVolumeAtExtreme()
    {
        var finished = Bar((5000.00m, 5, 5), (5000.25m, 20, 0));   // high is one-sided (buy only)
        var unfinished = Bar((5000.00m, 5, 5), (5000.25m, 20, 8)); // high is two-sided

        Assert.False(FootprintFeatures.UnfinishedAuction(finished, atHigh: true, minVolume: 5));
        Assert.True(FootprintFeatures.UnfinishedAuction(unfinished, atHigh: true, minVolume: 5));
    }

    [Fact]
    public void DeltaPriceDivergence_FlagsDisagreement()
    {
        var b = new VolumeBarBuilder(Tick, Opts);
        b.OnTrade(Px(5000.00m), 1, Side.Bid);   // open
        b.OnTrade(Px(5000.50m), 1, Side.Bid);
        b.OnTrade(Px(5000.50m), 10, Side.Ask);  // heavy selling, but price closed up
        Assert.True(FootprintFeatures.DeltaPriceDivergence(b.Forming));

        var b2 = new VolumeBarBuilder(Tick, Opts);
        b2.OnTrade(Px(5000.00m), 1, Side.Ask);
        b2.OnTrade(Px(5000.50m), 10, Side.Bid); // price up, delta up — agreement
        Assert.False(FootprintFeatures.DeltaPriceDivergence(b2.Forming));
    }

    [Fact]
    public void HasSellImbalanceNear_FindsImbalanceWithinBandOnly()
    {
        // sell(5000.00)=30 vs buy(5000.25)=2 → 30 ≥ 3×2 → a sell imbalance at 5000.00
        var bar = Bar((5000.00m, 2, 30), (5000.25m, 2, 0));

        Assert.True(FootprintFeatures.HasSellImbalanceNear(bar, Tick, Px(5000.25m), withinTicks: 2, ratio: 3.0));
        Assert.False(FootprintFeatures.HasSellImbalanceNear(bar, Tick, Px(5001.00m), withinTicks: 1, ratio: 3.0));
    }

    [Fact]
    public void AggressorVolumeBeyond_SumsBuyVolumeAtOrAboveLevel()
    {
        var bar = Bar((5000.00m, 20, 5), (5000.25m, 30, 0), (5000.50m, 10, 0));
        // buy at or above 5000.25 = 30 + 10 = 40
        Assert.Equal(40, FootprintFeatures.AggressorVolumeBeyond(bar, Tick, Px(5000.25m), buySide: true, atOrAbove: true));
        // sell at or below 5000.00 = 5
        Assert.Equal(5, FootprintFeatures.AggressorVolumeBeyond(bar, Tick, Px(5000.00m), buySide: false, atOrAbove: false));
    }

    [Fact]
    public void StackedImbalanceLen_DirectionalSellRun()
    {
        // two consecutive sell imbalances near the top, buy side quiet
        var bar = Bar(
            (5000.00m, 1, 1),
            (5000.25m, 1, 30),  // sell imb (sell 30 vs buy@5000.50)
            (5000.50m, 1, 30),  // sell imb (sell 30 vs buy@5000.75 floored)
            (5000.75m, 1, 1));

        Assert.Equal(2, FootprintFeatures.StackedImbalanceLen(bar, Tick, ratio: 3.0, sellSide: true));
        Assert.Equal(0, FootprintFeatures.StackedImbalanceLen(bar, Tick, ratio: 3.0, sellSide: false));
    }

    [Fact]
    public void HasBuyImbalanceNear_MirrorsForBuySide()
    {
        // buy(5000.25)=30 vs sell(5000.00)=2 → 30 ≥ 3×2 → a buy imbalance at 5000.25
        var bar = Bar((5000.00m, 0, 2), (5000.25m, 30, 2));

        Assert.True(FootprintFeatures.HasBuyImbalanceNear(bar, Tick, Px(5000.00m), withinTicks: 2, ratio: 3.0));
        Assert.False(FootprintFeatures.HasBuyImbalanceNear(bar, Tick, Px(4999.00m), withinTicks: 1, ratio: 3.0));
    }

    [Fact]
    public void PocDrift_OverLastFiveCompletedBars()
    {
        var b = new VolumeBarBuilder(Tick, new FeatureEngineOptions { BarVolumeSize = 10, PocDriftBars = 5 });
        // five completed bars whose POCs step up one tick each: 5000.00 .. 5001.00
        for (int i = 0; i < 5; i++)
        {
            decimal px = 5000.00m + i * 0.25m;
            Assert.Null(FootprintFeatures.PocDriftTicks(b, Tick)); // not enough bars yet
            b.OnTrade(Px(px), 10, Side.Bid);
        }

        // POC(latest) − POC(latest−4) = 4 ticks
        Assert.Equal(4, FootprintFeatures.PocDriftTicks(b, Tick));
    }
}
