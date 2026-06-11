namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 4 (LVN vacuum continuation) thresholds — rulebook D1–D4, entry, stop, target and
/// time stop, every number a named config value (appsettings "Detectors:Setup4"). Defaults
/// are the rulebook's stated ES values. Stated for the downside (short) version; the long
/// mirror flips signs, not thresholds.
/// </summary>
public sealed class LvnVacuumOptions
{
    public bool Enabled { get; set; } = true;

    // ----- D1: context — sitting on an LVN with room to the next HVN -----

    /// <summary>D1: max ticks between last price and the profile LVN it is about to fall through (3).</summary>
    public int LvnProximityTicks { get; set; } = 3;

    /// <summary>D1: required distance from the LVN to the next HVN in the trade direction —
    /// "the move must have room to pay" (8 ticks).</summary>
    public int HvnRoomTicks { get; set; } = 8;

    // ----- D2: pulling -----

    /// <summary>D2: required fractional decline of displayed size on the pulling side over the
    /// depth-change window (40%). MBP-10 approximation: measured by F21 (depth change in the
    /// levels beyond the best, <see cref="FeatureEngineOptions.DepthChangeLevels"/> levels over
    /// <see cref="FeatureEngineOptions.DepthChangeLagSeconds"/>) rather than the rulebook's exact
    /// "5 levels below last price"; documented per CLAUDE.md.</summary>
    public double DepthDeclineFraction { get; set; } = 0.40;

    /// <summary>D2: pull ratio (F16 cancel ÷ add) above which cancels dominate adds (1.5).</summary>
    public double PullRatioMin { get; set; } = 1.5;

    // ----- D3: aggressor alignment -----

    /// <summary>D3: flow window the alignment delta is measured over (30 s).</summary>
    public int DeltaWindowSeconds { get; set; } = 30;

    /// <summary>D3: minimum aggressor volume pushing the trade direction (100 contracts) —
    /// directional delta = Sign × windowed delta, so the short reads the rulebook's −100.</summary>
    public long MinAlignedDeltaContracts { get; set; } = 100;

    // ----- entry / stop / target / time -----

    /// <summary>Entry: stop-market this many ticks beyond the LVN in the trade direction (1).
    /// The trigger realizes D4 (best ticks through the LVN).</summary>
    public int EntryOffsetTicks { get; set; } = 1;

    /// <summary>Stop: this many ticks beyond the LVN against the trade — "1 tick above the upper
    /// edge of the LVN zone (typically 3–4 ticks)". v1 treats the LVN as a single price and the
    /// zone width as this config (documented rulebook ambiguity).</summary>
    public int StopOffsetTicks { get; set; } = 4;

    /// <summary>Target: front-run the next HVN by this many ticks, exit 100% (1).</summary>
    public int TargetFrontRunTicks { get; set; } = 1;

    /// <summary>Time stop: a vacuum that has not accelerated by now isn't a vacuum (3 min).</summary>
    public int TimeStopSeconds { get; set; } = 180;

    /// <summary>Working stop-entry lifetime before it is cancelled unfilled. Not in the rulebook
    /// (the rulebook gives no entry expiry for Setup 4) — an engineering bound.</summary>
    public int EntryExpirySeconds { get; set; } = 60;
}
