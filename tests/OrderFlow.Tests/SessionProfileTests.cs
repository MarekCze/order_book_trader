using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class SessionProfileTests
{
    private static readonly TickSize Tick = TickSize.Es;

    private static Price Px(decimal p) => Price.FromDecimal(p);

    private static SessionProfile Profile(params (decimal Px, long Vol)[] bins)
    {
        var profile = new SessionProfile(Tick, new FeatureEngineOptions());
        foreach (var (px, vol) in bins)
        {
            profile.AddTrade(Px(px), vol);
        }
        return profile;
    }

    [Fact]
    public void Poc_IsMaxVolumeBin_TieResolvesLower()
    {
        var p = Profile((5000.00m, 10), (5000.25m, 30), (5000.50m, 30));
        Assert.Equal(Px(5000.25m), p.Poc);
    }

    [Fact]
    public void EmptyProfile_HasNoPocOrValueArea()
    {
        var p = Profile();
        Assert.Null(p.Poc);
        Assert.Null(p.ValueArea());
    }

    [Fact]
    public void ValueArea_SmallestContiguous70PercentAroundPoc()
    {
        // volumes 10, 20, 40, 20, 10 → POC 5000.50; expand: +5000.25 (tie→lower, 60),
        // +5000.75 (20 ≥ 10, 80 ≥ 70%) → VAL 5000.25, VAH 5000.75
        var p = Profile((5000.00m, 10), (5000.25m, 20), (5000.50m, 40), (5000.75m, 20), (5001.00m, 10));

        var va = p.ValueArea()!.Value;
        Assert.Equal(Px(5000.25m), va.Val);
        Assert.Equal(Px(5000.75m), va.Vah);
    }

    [Fact]
    public void MeanPerPriceVolume_OverDistinctTradedPrices()
    {
        var p = Profile((5000.00m, 40), (5000.25m, 2), (5000.50m, 42));
        Assert.Equal(28.0, p.MeanPerPriceVolume!.Value, precision: 10);
    }

    [Fact]
    public void Lvn_LocalMinimumBelowQuarterOfMean()
    {
        // mean = 82/3 ≈ 27.33, threshold ≈ 6.83; the 2-lot middle bin is a local min
        var p = Profile((5000.00m, 40), (5000.25m, 2), (5000.50m, 40));

        Assert.True(p.IsLvn(Px(5000.25m)));
        Assert.False(p.IsLvn(Px(5000.00m))); // edge bins are never LVNs
        Assert.False(p.IsLvn(Px(5000.50m)));
        Assert.Equal(new[] { Px(5000.25m) }, p.Lvns());
    }

    [Fact]
    public void Lvn_ZeroVolumeGapInsideRange_Counts()
    {
        // untraded gap at 5000.25 between two traded bins
        var p = Profile((5000.00m, 40), (5000.50m, 40));
        Assert.True(p.IsLvn(Px(5000.25m)));
    }

    [Fact]
    public void NextHvn_NearestQualifyingLocalMaxInDirection()
    {
        // mean = 102/3 = 34, HVN threshold (1.5×) = 51 → only the 60-lot bin qualifies
        var p = Profile((5000.00m, 40), (5000.25m, 2), (5000.50m, 60));

        Assert.Equal(Px(5000.50m), p.NextHvn(Px(5000.00m), up: true));
        Assert.Null(p.NextHvn(Px(5000.50m), up: true));   // nothing beyond
        Assert.Null(p.NextHvn(Px(5000.50m), up: false));  // 40-lot bin is below threshold
    }

    [Fact]
    public void VolumeAt_AndTotals()
    {
        var p = Profile((5000.00m, 40), (5000.00m, 10), (5000.25m, 5));
        Assert.Equal(50, p.VolumeAt(Px(5000.00m)));
        Assert.Equal(55, p.TotalVolume);
    }
}
