namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 5 transition guards as pure functions over direction-normalized inputs (the detector
/// picks the high-side divergence for the stated short, the low-side for the long mirror, and
/// flips signs). "Extension" is how far the new extreme ran past the prior swing the adverse
/// way; "cumDeltaGap" is positive when the new extreme printed with less conviction than the
/// prior one; "directionalRangePos" is the close's position toward the extreme (1 = at it).
/// Each maps 1:1 to a rulebook condition.
/// </summary>
public static class Setup5Guards
{
    /// <summary>E1: the new 30-minute extreme exceeds the prior swing extreme by ≥ MinNewExtremeTicks.</summary>
    public static bool E1_NewExtremeBeyondPrior(long extensionTicks, Setup5Options o) =>
        extensionTicks >= o.MinNewExtremeTicks;

    /// <summary>E2: cumDelta diverged (the new extreme printed with less conviction), OR the bar
    /// printing the extreme has non-confirming delta yet closed in the extreme third of its range.</summary>
    public static bool E2_Divergence(
        long cumDeltaGap, bool barDeltaNonConfirming, double directionalRangePos, Setup5Options o) =>
        cumDeltaGap > 0
        || (barDeltaNonConfirming && directionalRangePos >= o.CloseInExtremeFraction);

    /// <summary>E3: the new extreme is within LocationProximityTicks of an LOI.</summary>
    public static bool E3_Location(long? loiDistanceTicks, Setup5Options o) =>
        loiDistanceTicks is { } d && Math.Abs(d) <= o.LocationProximityTicks;

    /// <summary>E4: a diagonal imbalance printed within ImbalanceProximityTicks of the extreme,
    /// OR price has reclaimed past the prior swing extreme.</summary>
    public static bool E4_Trigger(bool imbalanceNearExtreme, bool reclaimedPriorExtreme) =>
        imbalanceNearExtreme || reclaimedPriorExtreme;

    /// <summary>Invalidation: a DeltaBucketSeconds bucket printed ≥ InvalidationDeltaContracts of
    /// aggressor delta the adverse way beyond the prior swing extreme — real participants showed up.</summary>
    public static bool Invalidation_RealAggressors(long adverseBucketDelta, Setup5Options o) =>
        adverseBucketDelta >= o.InvalidationDeltaContracts;
}
