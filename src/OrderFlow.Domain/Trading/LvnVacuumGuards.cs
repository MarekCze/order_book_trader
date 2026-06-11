namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 4 transition guards as pure functions over direction-normalized inputs (the
/// detector flips signs for the mirror). "Continuation" is the trade direction — down for
/// the rulebook's stated short, up for the long mirror. Directional delta is signed
/// positive when aggressors push the continuation way. Each maps 1:1 to a rulebook
/// condition; D4 (best ticks through the LVN) is realized by the entry stop-market trigger
/// at the LVN ± 1 tick, not a separate guard.
/// </summary>
public static class LvnVacuumGuards
{
    /// <summary>D1: the next HVN in the trade direction is ≥ HvnRoomTicks beyond the LVN —
    /// the vacuum has room to pay. The "within LvnProximityTicks of the LVN" half of D1 is the
    /// nearest-LVN scan that produces the level (<see cref="Features.SessionProfile.TryGetNearestLvn"/>).</summary>
    public static bool D1_RoomToPay(long lvnToHvnTicks, LvnVacuumOptions o) =>
        lvnToHvnTicks >= o.HvnRoomTicks;

    /// <summary>D2: displayed size on the pulling side has declined ≥ DepthDeclineFraction over
    /// the window AND cancels dominate adds (pull ratio > PullRatioMin). Both null inputs
    /// (no depth history / nothing added) fail closed.</summary>
    public static bool D2_Pulling(double? depthChangeFraction, double? pullRatio, LvnVacuumOptions o) =>
        depthChangeFraction is { } dc && dc <= -o.DepthDeclineFraction
        && pullRatio is { } pr && pr > o.PullRatioMin;

    /// <summary>D3: aggressors are aligned with the continuation — directional delta
    /// (Sign × windowed delta) ≥ MinAlignedDeltaContracts.</summary>
    public static bool D3_AggressorAlignment(long directionalDelta, LvnVacuumOptions o) =>
        directionalDelta >= o.MinAlignedDeltaContracts;
}
