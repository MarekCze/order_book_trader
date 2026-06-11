using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>Setup 2 (stop-run fade) transition guards as pure functions over direction-normalized
/// inputs: the detector fades a swept high for the stated short and a swept low for the long
/// mirror, so "beyond" always means in the sweep direction and "supply" the resting side ahead.</summary>
public class StopRunFadeGuardTests
{
    private static readonly Setup2Options O = new();

    [Fact]
    public void B2_InSweepZone_OneToFiveTicks()
    {
        Assert.True(Setup2Guards.B2_InSweepZone(1, O));
        Assert.True(Setup2Guards.B2_InSweepZone(5, O));
        Assert.False(Setup2Guards.B2_InSweepZone(0, O)); // not above the level
        Assert.False(Setup2Guards.B2_InSweepZone(6, O)); // a break > 5 ticks is a breakout
    }

    [Fact]
    public void B3_Climax_NeedsPercentileVolumeAndShareAtLevel()
    {
        // bar aggressive volume 900 ≥ P90 (800); 600 of it at/above H ≥ 60% of 900 (540)
        Assert.True(Setup2Guards.B3_Climax(900, volumeP90: 800, volumeBeyond: 600, totalAggressive: 900, O));
        Assert.False(Setup2Guards.B3_Climax(700, 800, 600, 900, O));   // below the climax percentile
        Assert.False(Setup2Guards.B3_Climax(900, 800, 500, 900, O));   // < 60% executed at/above H
        Assert.False(Setup2Guards.B3_Climax(900, null, 600, 900, O));  // distribution not ready
        Assert.False(Setup2Guards.B3_Climax(0, 800, 0, 0, O));         // no volume
    }

    [Fact]
    public void B4_NoFollowThrough_AllowsAtMostOneTickBeyondSweep()
    {
        Assert.True(Setup2Guards.B4_NoFollowThrough(0, O));
        Assert.True(Setup2Guards.B4_NoFollowThrough(1, O));
        Assert.False(Setup2Guards.B4_NoFollowThrough(2, O)); // a new high 2 ticks beyond the sweep
    }

    [Fact]
    public void B5_SupplyConfirms_EitherStackingOrImbalance()
    {
        Assert.True(Setup2Guards.B5_SupplyConfirms(0.60, stackedImbalanceLen: 0, O));  // offers stacked ≥ 50%
        Assert.True(Setup2Guards.B5_SupplyConfirms(0.10, stackedImbalanceLen: 3, O));  // 3 stacked sell imbalances
        Assert.False(Setup2Guards.B5_SupplyConfirms(0.10, stackedImbalanceLen: 2, O)); // neither
        Assert.False(Setup2Guards.B5_SupplyConfirms(null, stackedImbalanceLen: 2, O)); // no depth history, too few imbalances
    }
}
