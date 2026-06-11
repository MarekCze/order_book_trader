namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 2 (stop-run fade) thresholds — rulebook B1–B6, entry, stop, targets and the scratch
/// rule, every number a named config value (appsettings "Detectors:Setup2"). Defaults are the
/// rulebook's stated ES values. Stated for the short (fade a swept high); the long mirror flips
/// signs, not thresholds.
/// </summary>
public sealed class Setup2Options
{
    public bool Enabled { get; set; } = true;

    // ----- B1/B2: the sweep -----

    /// <summary>B2: minimum ticks the sweep pokes beyond the reference level (1).</summary>
    public int SweepZoneMinTicks { get; set; } = 1;

    /// <summary>B2: maximum ticks of the sweep zone — a break beyond this is a breakout, not a
    /// sweep, and is not faded (5).</summary>
    public int SweepZoneMaxTicks { get; set; } = 5;

    // ----- B3: climax -----

    /// <summary>B3: the breaking bar's aggressive volume must reach this session-bar percentile (90th).</summary>
    public double ClimaxVolumePercentile { get; set; } = 0.90;

    /// <summary>B3: fraction of the breaking bar's aggressive volume that must execute at or beyond
    /// the reference level (60%).</summary>
    public double ClimaxShareBeyondLevel { get; set; } = 0.60;

    // ----- B4: no follow-through -----

    /// <summary>B4: the most a new high may extend beyond the sweep high before it counts as
    /// follow-through (a continuation, not a trap) — 1 tick.</summary>
    public int FollowThroughTicks { get; set; } = 1;

    // ----- B5: supply confirms -----

    /// <summary>B5: required increase in displayed offer size beyond the sweep since it printed (50%).
    /// MBP-10 approximation: read from F21 (depth change in the levels beyond the best) rather than
    /// "the 3 levels above the sweep high" exactly; documented per CLAUDE.md.</summary>
    public double SupplyDepthIncrease { get; set; } = 0.50;

    /// <summary>B5: alternative confirmation — this many consecutive diagonal sell imbalances (3).</summary>
    public int StackedImbalanceMinLen { get; set; } = 3;

    /// <summary>B5: diagonal imbalance ratio (3:1).</summary>
    public double ImbalanceRatio { get; set; } = 3.0;

    // ----- entry / stop / targets -----

    /// <summary>Entry: sell stop this many ticks inside the reference level (2 → sell stop at H − 2).
    /// Its trigger realizes B6 (price reclaiming past H − 1).</summary>
    public int EntryOffsetTicks { get; set; } = 2;

    /// <summary>Entry: cancel the resting stop if it has not triggered within this long of the sweep (4 min).</summary>
    public int SweepValiditySeconds { get; set; } = 240;

    /// <summary>Stop: this many ticks beyond the sweep high against the trade (1).</summary>
    public int StopAboveSweepTicks { get; set; } = 1;

    /// <summary>T1 at this multiple of R (1R), exiting <see cref="T1ExitFraction"/>.</summary>
    public double T1RMultiple { get; set; } = 1.0;

    /// <summary>Fraction exited at T1 (50%).</summary>
    public double T1ExitFraction { get; set; } = 0.5;

    /// <summary>After T1: stop moves to entry ∓ this offset. Rulebook says "stop to entry" (0).</summary>
    public int BreakevenOffsetTicks { get; set; } = 0;

    /// <summary>T2 cap in R (4R); the developing POC (a proxy for "origin of the breakout leg",
    /// which is not computed in v1 — documented) caps below it.</summary>
    public double T2RCap { get; set; } = 4.0;

    // ----- scratch -----

    /// <summary>Scratch: if price re-crosses past the reference level and holds for this long without
    /// hitting the stop, the trap thesis is wrong → exit at market (60 s).</summary>
    public int ScratchSeconds { get; set; } = 60;
}
