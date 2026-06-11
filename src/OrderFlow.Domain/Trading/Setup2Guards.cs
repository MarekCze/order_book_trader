namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 2 transition guards as pure functions over direction-normalized inputs (the detector
/// fades a swept high for the stated short, a swept low for the long mirror). "Beyond" is the
/// sweep direction; "aggressive volume" is buying for the short, selling for the long. Each maps
/// 1:1 to a rulebook condition; B1 (a reference level exists) and B6 (the reclaim trigger) are
/// realized by the engine's swept-reference query and the entry stop trigger respectively.
/// </summary>
public static class Setup2Guards
{
    /// <summary>B2: the sweep poked beyond the reference level by 1..SweepZoneMaxTicks; a deeper
    /// break is a breakout, not a sweep.</summary>
    public static bool B2_InSweepZone(long ticksBeyondLevel, Setup2Options o) =>
        ticksBeyondLevel >= o.SweepZoneMinTicks && ticksBeyondLevel <= o.SweepZoneMaxTicks;

    /// <summary>B3: the breaking bar's aggressive volume reached the climax percentile, and ≥
    /// ClimaxShareBeyondLevel of it executed at or beyond the reference level.</summary>
    public static bool B3_Climax(
        long barAggressiveVolume, long? volumeP90, long volumeBeyond, long totalAggressive, Setup2Options o) =>
        volumeP90 is { } p90
        && barAggressiveVolume >= p90
        && totalAggressive > 0
        && volumeBeyond >= o.ClimaxShareBeyondLevel * totalAggressive;

    /// <summary>B4: no follow-through — the max new high since the sweep is within FollowThroughTicks
    /// of the sweep high.</summary>
    public static bool B4_NoFollowThrough(long maxTicksBeyondSweep, Setup2Options o) =>
        maxTicksBeyondSweep <= o.FollowThroughTicks;

    /// <summary>B5: supply confirms — displayed liquidity ahead stacked ≥ SupplyDepthIncrease, OR
    /// ≥ StackedImbalanceMinLen consecutive diagonal imbalances printed.</summary>
    public static bool B5_SupplyConfirms(double? supplyDepthChange, int stackedImbalanceLen, Setup2Options o) =>
        (supplyDepthChange is { } d && d >= o.SupplyDepthIncrease)
        || stackedImbalanceLen >= o.StackedImbalanceMinLen;
}
