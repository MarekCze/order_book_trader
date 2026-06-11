namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 1 transition guards as pure functions over direction-normalized inputs (the
/// detector flips signs for the short mirror): "decline"/"advance" are moves toward/away
/// from the defended level, "sell" is aggressor flow into it, and favorable delta is
/// signed positive in the fade direction. Each maps 1:1 to a rulebook condition.
/// </summary>
public static class AbsorptionGuards
{
    /// <summary>A1: price declined ≥ DeclineTicks over the window and is within
    /// ContextLoiProximityTicks of an LOI.</summary>
    public static bool A1_DeclineIntoLevel(long declineTicks, long? loiDistanceTicks, Setup1Options o) =>
        declineTicks >= o.DeclineTicks
        && loiDistanceTicks is { } d
        && Math.Abs(d) <= o.ContextLoiProximityTicks;

    /// <summary>A2: windowed delta ≤ −DeltaContracts, or in the session distribution's
    /// adverse tail (≤ DeltaPercentile). <paramref name="tailRank"/> is the
    /// direction-normalized rank (long: P[X ≤ delta]; short: P[X ≥ delta]).</summary>
    public static bool A2_AggressiveFlow(long directionalDelta, double? tailRank, Setup1Options o) =>
        directionalDelta <= -o.DeltaContracts
        || (tailRank is { } r && r <= o.DeltaPercentile);

    /// <summary>A3: the defended best has held L for ≥ StallSeconds (held-invariant is
    /// enforced by the state machine resetting otherwise) despite continued sell prints.</summary>
    public static bool A3_PriceStalls(long stallNanos, long sellPrintsInStall, Setup1Options o) =>
        stallNanos >= o.StallSeconds * 1_000_000_000L
        && sellPrintsInStall >= o.MinStallSellPrints;

    /// <summary>A4: aggressor volume at [L, L+1] over the stall ≥ StallVolumeMultiple × the
    /// prior 15-minute per-price mean.</summary>
    public static bool A4_VolumeWithoutProgress(long volumeAtLevel, double? baselinePerPriceVolume, Setup1Options o) =>
        baselinePerPriceVolume is { } baseline
        && baseline > 0
        && volumeAtLevel >= o.StallVolumeMultiple * baseline;

    /// <summary>A5: traded(L) ÷ max displayed(L) ≥ ReplenishRatioMin with ≥ RefreshCountMin
    /// refresh events at L.</summary>
    public static bool A5_Replenishment(double? replenishRatio, int refreshCount, Setup1Options o) =>
        replenishRatio is { } ratio
        && ratio >= o.ReplenishRatioMin
        && refreshCount >= o.RefreshCountMin;

    /// <summary>A6: latest completed aggressor bucket fell ≥ ExhaustionDropRatio from the
    /// stall's peak bucket AND that bucket's delta is non-negative in the fade direction.</summary>
    public static bool A6_Exhaustion(
        int completedBuckets, long peakBucketVolume, long lastBucketVolume, long lastBucketFavorableDelta, Setup1Options o) =>
        completedBuckets >= 2
        && peakBucketVolume > 0
        && lastBucketVolume <= (1.0 - o.ExhaustionDropRatio) * peakBucketVolume
        && lastBucketFavorableDelta >= 0;

    /// <summary>Entry: after LimitWorkingSeconds unfilled with price ≥ L + MomentumMinAdvanceTicks,
    /// switch to the momentum stop entry.</summary>
    public static bool ShouldEscalateToMomentum(long nanosSinceArm, long advanceTicks, Setup1Options o) =>
        nanosSinceArm >= o.LimitWorkingSeconds * 1_000_000_000L
        && advanceTicks >= o.MomentumMinAdvanceTicks;

    /// <summary>Entry: unfilled for EntryExpirySeconds — setup expired.</summary>
    public static bool EntryExpired(long nanosSinceArm, Setup1Options o) =>
        nanosSinceArm >= o.EntryExpirySeconds * 1_000_000_000L;

    /// <summary>Time stop: neither T1 nor stop within TimeStopSeconds (after T1 the
    /// remainder runs to T2 / the breakeven stop).</summary>
    public static bool TimeStopHit(long nanosSinceEntry, bool t1Filled, Setup1Options o) =>
        !t1Filled && nanosSinceEntry >= o.TimeStopSeconds * 1_000_000_000L;

    /// <summary>Invalidation: a single print or same-instant sweep of this size traded
    /// through L.</summary>
    public static bool SweepInvalidation(long sweepVolumeThroughLevel, Setup1Options o) =>
        sweepVolumeThroughLevel >= o.InvalidationSweepContracts;

    /// <summary>Invalidation: the absorbing level was pulled — its displayed size dropped
    /// more than InvalidationVanishRatio of the max seen since arm, beyond what trades at
    /// the level account for.</summary>
    public static bool LevelVanished(long maxDisplayedSinceArm, long displayedNow, long tradedSinceArm, Setup1Options o) =>
        maxDisplayedSinceArm > 0
        && maxDisplayedSinceArm - displayedNow - tradedSinceArm > o.InvalidationVanishRatio * maxDisplayedSinceArm;
}
