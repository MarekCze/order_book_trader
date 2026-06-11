using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Setup 1 (absorption fade) transition guards as pure functions (CLAUDE.md: every
/// rulebook condition is an explicit, individually testable guard). All guards operate in
/// direction-normalized "long" space: the detector flips signs for the short mirror, so
/// "decline" means movement toward the defended level and "sell" means aggressor flow
/// into it.
/// </summary>
public class AbsorptionGuardTests
{
    private static readonly Setup1Options O = new();

    private const long Nano = 1_000_000_000;

    // ----- A1: decline into a level -----

    [Fact]
    public void A1_RequiresBothDeclineAndLoiProximity()
    {
        Assert.True(AbsorptionGuards.A1_DeclineIntoLevel(6, 2, O));
        Assert.True(AbsorptionGuards.A1_DeclineIntoLevel(9, -2, O));
        Assert.False(AbsorptionGuards.A1_DeclineIntoLevel(5, 0, O));    // decline too small
        Assert.False(AbsorptionGuards.A1_DeclineIntoLevel(6, 3, O));    // too far from LOI
        Assert.False(AbsorptionGuards.A1_DeclineIntoLevel(6, null, O)); // no LOI known
    }

    // ----- A2: genuinely aggressive flow -----

    [Fact]
    public void A2_PassesOnAbsoluteThreshold_OrTailPercentile()
    {
        Assert.True(AbsorptionGuards.A2_AggressiveFlow(-300, 0.5, O));   // absolute
        Assert.True(AbsorptionGuards.A2_AggressiveFlow(-100, 0.10, O));  // 10th pctile tail
        Assert.False(AbsorptionGuards.A2_AggressiveFlow(-299, 0.11, O)); // neither
        Assert.False(AbsorptionGuards.A2_AggressiveFlow(300, 0.5, O));   // wrong direction
        Assert.True(AbsorptionGuards.A2_AggressiveFlow(-300, null, O));  // absolute works without pctl
        Assert.False(AbsorptionGuards.A2_AggressiveFlow(-100, null, O));
    }

    // ----- A3: price stalls with continued selling -----

    [Fact]
    public void A3_RequiresStallDuration_AndContinuedSellPrints()
    {
        Assert.True(AbsorptionGuards.A3_PriceStalls(45 * Nano, 1, O));
        Assert.False(AbsorptionGuards.A3_PriceStalls(44 * Nano, 10, O)); // too short
        Assert.False(AbsorptionGuards.A3_PriceStalls(60 * Nano, 0, O));  // no sell prints
    }

    // ----- A4: volume without progress -----

    [Fact]
    public void A4_RequiresStallVolumeMultipleOfBaseline()
    {
        Assert.True(AbsorptionGuards.A4_VolumeWithoutProgress(60, 20.0, O));
        Assert.False(AbsorptionGuards.A4_VolumeWithoutProgress(59, 20.0, O));
        Assert.False(AbsorptionGuards.A4_VolumeWithoutProgress(60, null, O)); // baseline not ready
        Assert.False(AbsorptionGuards.A4_VolumeWithoutProgress(0, 0.0, O));   // empty baseline
    }

    // ----- A5: replenishment -----

    [Fact]
    public void A5_RequiresReplenishRatio_AndRefreshCount()
    {
        Assert.True(AbsorptionGuards.A5_Replenishment(2.5, 3, O));
        Assert.False(AbsorptionGuards.A5_Replenishment(2.4, 5, O));
        Assert.False(AbsorptionGuards.A5_Replenishment(3.0, 2, O));
        Assert.False(AbsorptionGuards.A5_Replenishment(null, 3, O));
    }

    // ----- A6: exhaustion trigger -----

    [Fact]
    public void A6_RequiresSellBucketCollapse_AndNonNegativeLastDelta()
    {
        // peak 100 → last ≤ 30 with the default 70% drop
        Assert.True(AbsorptionGuards.A6_Exhaustion(completedBuckets: 3, peakBucketVolume: 100, lastBucketVolume: 30, lastBucketFavorableDelta: 0, O));
        Assert.False(AbsorptionGuards.A6_Exhaustion(3, 100, 31, 0, O));   // not enough decay
        Assert.False(AbsorptionGuards.A6_Exhaustion(3, 100, 30, -1, O));  // sellers still net active
        Assert.False(AbsorptionGuards.A6_Exhaustion(1, 100, 0, 0, O));    // need ≥ 2 completed buckets
        Assert.False(AbsorptionGuards.A6_Exhaustion(3, 0, 0, 0, O));      // no selling at all = nothing to exhaust
    }

    // ----- entry management -----

    [Fact]
    public void Escalation_After30s_WhenPriceHeldAboveLevel()
    {
        Assert.True(AbsorptionGuards.ShouldEscalateToMomentum(30 * Nano, advanceTicks: 2, O));
        Assert.False(AbsorptionGuards.ShouldEscalateToMomentum(29 * Nano, 2, O));
        Assert.False(AbsorptionGuards.ShouldEscalateToMomentum(30 * Nano, 1, O));
    }

    [Fact]
    public void EntryExpiry_At90s()
    {
        Assert.True(AbsorptionGuards.EntryExpired(90 * Nano, O));
        Assert.False(AbsorptionGuards.EntryExpired(89 * Nano, O));
    }

    // ----- position management -----

    [Fact]
    public void TimeStop_OnlyBeforeT1()
    {
        Assert.True(AbsorptionGuards.TimeStopHit(300 * Nano, t1Filled: false, O));
        Assert.False(AbsorptionGuards.TimeStopHit(299 * Nano, false, O));
        Assert.False(AbsorptionGuards.TimeStopHit(600 * Nano, true, O)); // after T1 the trade runs
    }

    [Fact]
    public void SweepInvalidation_At200Contracts()
    {
        Assert.True(AbsorptionGuards.SweepInvalidation(200, O));
        Assert.False(AbsorptionGuards.SweepInvalidation(199, O));
    }
}
