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

    // ----- Option A: extreme-flow (climax) gate -----

    [Fact]
    public void FlowNotClimaxing_DisabledByDefault_AlwaysPasses()
    {
        // Default options leave the gate off → it must never block, regardless of flow (byte-identical baseline).
        Assert.True(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: 99.0, O));
        Assert.True(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: null, O));
    }

    [Fact]
    public void FlowNotClimaxing_Enabled_BlocksWhenFlowIsExtreme()
    {
        var o = new Setup5Options { FlowClimaxGateEnabled = true, MaxTriggerFlowZ = 10.0 };
        Assert.False(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: 15.0, o)); // mid-climax: with-move buy flow at +15σ
        Assert.False(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: 10.0, o)); // at the threshold → blocked
        Assert.True(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: 9.9, o));   // below threshold → fade allowed
        Assert.True(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: -20.0, o)); // flow has flipped against the move → allow
    }

    [Fact]
    public void FlowNotClimaxing_Enabled_PassesThroughWhenZScoreUnavailable()
    {
        var o = new Setup5Options { FlowClimaxGateEnabled = true, MaxTriggerFlowZ = 10.0 };
        Assert.True(Setup5Guards.FlowNotClimaxing(withMoveFlowZ: null, o)); // distribution not ready → cannot judge → pass
    }

    // ----- Option B: deceleration (roll-over from peak) gate -----

    [Fact]
    public void FlowDecelerated_DisabledByDefault_AlwaysPasses()
    {
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: 1000, O));
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 0, lastWithMoveDelta: null, O));
    }

    [Fact]
    public void FlowDecelerated_Enabled_RequiresLastBucketBelowPeakByDropRatio()
    {
        var o = new Setup5Options { FlowDecelGateEnabled = true, ExhaustionDropRatio = 0.70 };
        // peak 1000 → must have dropped ≥70% → last ≤ 300 passes.
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: 250, o));
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: 300, o));
        Assert.False(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: 500, o)); // still pushing
        Assert.False(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: 301, o));
    }

    [Fact]
    public void FlowDecelerated_Enabled_PassesThroughWithInsufficientHistory()
    {
        var o = new Setup5Options { FlowDecelGateEnabled = true, ExhaustionDropRatio = 0.70 };
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 0, lastWithMoveDelta: 999, o));   // no real push yet
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: -50, lastWithMoveDelta: 10, o));  // peak ≤ 0
        Assert.True(Setup5Guards.FlowDecelerated(peakWithMoveDelta: 1000, lastWithMoveDelta: null, o)); // no completed bucket
    }
}
