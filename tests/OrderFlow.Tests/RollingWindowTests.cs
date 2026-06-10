using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class FlowWindowRingTests
{
    private static Timestamp Sec(long s) => new((ulong)s * 1_000_000_000UL);

    private static FlowSample Buy(long vol) => new(vol, 0, 1, 0, 0);
    private static FlowSample Sell(long vol) => new(0, vol, 1, 0, 0);

    [Fact]
    public void SamplesInCurrentSecond_AppearInAllWindows()
    {
        var ring = new FlowWindowRing(new[] { 10, 60 });
        ring.Advance(Sec(100));
        ring.Add(Buy(5));
        ring.Add(Sell(3));

        foreach (var w in new[] { 0, 1 })
        {
            var agg = ring.WindowSum(w);
            Assert.Equal(5, agg.BuyVolume);
            Assert.Equal(3, agg.SellVolume);
            Assert.Equal(2, agg.TradeCount);
            Assert.Equal(2, agg.Delta);
            Assert.Equal(8, agg.Volume);
        }
    }

    [Fact]
    public void OldSamples_AgeOutOfShortWindowButNotLong()
    {
        var ring = new FlowWindowRing(new[] { 10, 60 });
        ring.Advance(Sec(100));
        ring.Add(Buy(5));

        ring.Advance(Sec(115)); // 15s later: out of the 10s window, inside the 60s window

        Assert.Equal(0, ring.WindowSum(0).BuyVolume);
        Assert.Equal(5, ring.WindowSum(1).BuyVolume);
    }

    [Fact]
    public void Window_IncludesExactlyLastWSeconds()
    {
        var ring = new FlowWindowRing(new[] { 10 });
        ring.Advance(Sec(100));
        ring.Add(Buy(1));

        ring.Advance(Sec(109)); // age 9s: still inside a 10s window
        Assert.Equal(1, ring.WindowSum(0).BuyVolume);

        ring.Advance(Sec(110)); // age 10s: evicted
        Assert.Equal(0, ring.WindowSum(0).BuyVolume);
    }

    [Fact]
    public void GapLargerThanMaxWindow_ClearsEverything()
    {
        var ring = new FlowWindowRing(new[] { 10, 300 });
        ring.Advance(Sec(100));
        ring.Add(Buy(7));

        ring.Advance(Sec(100 + 100_000)); // weekend-sized gap

        Assert.Equal(0, ring.WindowSum(1).Volume);
    }

    [Fact]
    public void SumSecondsInclusive_SlicesRetainedHistory()
    {
        var ring = new FlowWindowRing(new[] { 300 });
        for (long s = 0; s < 25; s++)
        {
            ring.Advance(Sec(1000 + s));
            ring.Add(Sell(s + 1)); // second 1000+s carries sell volume s+1
        }

        // seconds 1010..1019 carry 11+12+...+20 = 155
        Assert.Equal(155, ring.SumSecondsInclusive(1010, 1019).SellVolume);
        // current second included
        Assert.Equal(25, ring.SumSecondsInclusive(1024, 1024).SellVolume);
    }
}

public class RollingStatsTests
{
    private static Timestamp Sec(long s) => new((ulong)s * 1_000_000_000UL);

    [Fact]
    public void MeanAndStd_OverKnownSamples()
    {
        var stats = new RollingStats(windowSeconds: 60);
        long[] values = { 2, 4, 4, 4, 5, 5, 7, 9 }; // mean 5, sample std sqrt(32/7)
        for (int i = 0; i < values.Length; i++)
        {
            stats.Add(Sec(100 + i), values[i]);
        }

        Assert.Equal(8, stats.Count);
        Assert.Equal(5.0, stats.Mean!.Value, precision: 10);
        Assert.Equal(Math.Sqrt(32.0 / 7.0), stats.SampleStd!.Value, precision: 10);
        Assert.Equal((9.0 - 5.0) / Math.Sqrt(32.0 / 7.0), stats.ZScore(9)!.Value, precision: 10);
    }

    [Fact]
    public void Samples_AgeOutOfWindow()
    {
        var stats = new RollingStats(windowSeconds: 10);
        stats.Add(Sec(100), 1000);
        stats.Add(Sec(109), 4);
        stats.Add(Sec(110), 6); // second 100 now evicted

        Assert.Equal(2, stats.Count);
        Assert.Equal(5.0, stats.Mean!.Value, precision: 10);
    }

    [Fact]
    public void Empty_ReturnsNulls()
    {
        var stats = new RollingStats(windowSeconds: 10);
        Assert.Null(stats.Mean);
        Assert.Null(stats.ZScore(5));
    }

    [Fact]
    public void ZeroVariance_ZScoreNull()
    {
        var stats = new RollingStats(windowSeconds: 10);
        stats.Add(Sec(1), 5);
        stats.Add(Sec(2), 5);
        Assert.Null(stats.ZScore(5));
    }
}

public class MonotonicWindowExtremeTests
{
    private static Timestamp Sec(long s) => new((ulong)s * 1_000_000_000UL);

    [Fact]
    public void Max_TracksRunningMaximum()
    {
        var max = new MonotonicWindowExtreme(windowSeconds: 60, trackMax: true);
        max.Add(Sec(100), 10);
        max.Add(Sec(101), 30);
        max.Add(Sec(102), 20);

        Assert.Equal(30, max.Extreme);
    }

    [Fact]
    public void Max_EvictionRevealsLowerMaximum()
    {
        var max = new MonotonicWindowExtreme(windowSeconds: 10, trackMax: true);
        max.Add(Sec(100), 30);
        max.Add(Sec(105), 20);
        max.Add(Sec(111), 10); // 100 is now > 10s old

        Assert.Equal(20, max.Extreme);
    }

    [Fact]
    public void Min_TracksRunningMinimum()
    {
        var min = new MonotonicWindowExtreme(windowSeconds: 60, trackMax: false);
        min.Add(Sec(100), 10);
        min.Add(Sec(101), 5);
        min.Add(Sec(102), 8);

        Assert.Equal(5, min.Extreme);
    }

    [Fact]
    public void Empty_HasNoExtreme()
    {
        var max = new MonotonicWindowExtreme(windowSeconds: 60, trackMax: true);
        Assert.Null(max.Extreme);
    }
}

public class VolumeByPriceWindowTests
{
    private static Timestamp Sec(long s) => new((ulong)s * 1_000_000_000UL);
    private static Price Px(decimal p) => Price.FromDecimal(p);

    [Fact]
    public void AccumulatesVolumePerPrice()
    {
        var w = new VolumeByPriceWindow(windowSeconds: 60);
        w.Add(Sec(100), Px(5000.00m), 10);
        w.Add(Sec(101), Px(5000.00m), 5);
        w.Add(Sec(102), Px(5000.25m), 7);

        Assert.Equal(15, w.VolumeAt(Px(5000.00m)));
        Assert.Equal(7, w.VolumeAt(Px(5000.25m)));
        Assert.Equal(0, w.VolumeAt(Px(4999.75m)));
        Assert.Equal(22, w.TotalVolume);
        Assert.Equal(2, w.DistinctPriceCount);
    }

    [Fact]
    public void Trades_AgeOutAndPricesDisappear()
    {
        var w = new VolumeByPriceWindow(windowSeconds: 10);
        w.Add(Sec(100), Px(5000.00m), 10);
        w.Add(Sec(105), Px(5000.25m), 4);
        w.Add(Sec(110), Px(5000.50m), 1); // evicts the t=100 trade

        Assert.Equal(0, w.VolumeAt(Px(5000.00m)));
        Assert.Equal(5, w.TotalVolume);
        Assert.Equal(2, w.DistinctPriceCount);
    }

    [Fact]
    public void MeanPerPriceVolume_IsTotalOverDistinct()
    {
        var w = new VolumeByPriceWindow(windowSeconds: 60);
        Assert.Null(w.MeanPerPriceVolume);

        w.Add(Sec(100), Px(5000.00m), 9);
        w.Add(Sec(100), Px(5000.25m), 3);

        Assert.Equal(6.0, w.MeanPerPriceVolume!.Value, precision: 10);
    }
}
