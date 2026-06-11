using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>Setup 5 (delta divergence fade) transition guards as pure functions over
/// direction-normalized inputs: the detector picks the high-side divergence for the stated
/// short and the low-side for the long mirror, and flips signs so every quantity below reads
/// the same in both directions.</summary>
public class DeltaDivergenceGuardTests
{
    private static readonly Setup5Options O = new();

    [Fact]
    public void E1_NewExtremeBeyondPrior_RequiresMinExtension()
    {
        Assert.True(Setup5Guards.E1_NewExtremeBeyondPrior(2, O));   // exactly 2 ticks beyond the prior swing
        Assert.True(Setup5Guards.E1_NewExtremeBeyondPrior(5, O));
        Assert.False(Setup5Guards.E1_NewExtremeBeyondPrior(1, O));  // only a 1-tick poke
        Assert.False(Setup5Guards.E1_NewExtremeBeyondPrior(-3, O)); // not actually a new extreme
    }

    [Fact]
    public void E2_Divergence_CumDeltaGapPath()
    {
        // cumDeltaGap > 0 means the new extreme printed with less conviction than the prior one.
        Assert.True(Setup5Guards.E2_Divergence(40, barDeltaNonConfirming: false, directionalRangePos: 0.0, O));
        Assert.False(Setup5Guards.E2_Divergence(0, barDeltaNonConfirming: false, directionalRangePos: 1.0, O)); // equal cumDelta, no bar signal
    }

    [Fact]
    public void E2_Divergence_BarPath_NeedsNonConfirmingDeltaAndExtremeClose()
    {
        Assert.True(Setup5Guards.E2_Divergence(-100, barDeltaNonConfirming: true, directionalRangePos: 0.70, O));  // ≥ 2/3
        Assert.False(Setup5Guards.E2_Divergence(-100, barDeltaNonConfirming: true, directionalRangePos: 0.50, O)); // not in the extreme third
        Assert.False(Setup5Guards.E2_Divergence(-100, barDeltaNonConfirming: false, directionalRangePos: 0.90, O)); // delta confirmed the move
    }

    [Fact]
    public void E3_Location_RequiresProximityToLoi()
    {
        Assert.True(Setup5Guards.E3_Location(4, O));
        Assert.True(Setup5Guards.E3_Location(-3, O));
        Assert.False(Setup5Guards.E3_Location(5, O));
        Assert.False(Setup5Guards.E3_Location(null, O));
    }

    [Fact]
    public void E4_Trigger_ImbalanceOrReclaim()
    {
        Assert.True(Setup5Guards.E4_Trigger(imbalanceNearExtreme: true, reclaimedPriorExtreme: false));
        Assert.True(Setup5Guards.E4_Trigger(imbalanceNearExtreme: false, reclaimedPriorExtreme: true));
        Assert.False(Setup5Guards.E4_Trigger(imbalanceNearExtreme: false, reclaimedPriorExtreme: false));
    }

    [Fact]
    public void Invalidation_FiresWhenRealAggressorsReturn()
    {
        Assert.True(Setup5Guards.Invalidation_RealAggressors(150, O));  // a 10s bucket of +150 the adverse way
        Assert.True(Setup5Guards.Invalidation_RealAggressors(300, O));
        Assert.False(Setup5Guards.Invalidation_RealAggressors(149, O));
    }
}
