namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 5 (delta divergence fade) thresholds — rulebook E1–E4, entry, stop, targets and the
/// real-aggressor invalidation, every number a named config value (appsettings
/// "Detectors:Setup5"). Defaults are the rulebook's stated ES values. Stated for the short
/// (divergence at highs); the long mirror flips signs, not thresholds.
/// </summary>
public sealed class Setup5Options
{
    public bool Enabled { get; set; } = true;

    // ----- E1: new extreme beyond the prior swing -----

    /// <summary>E1: the new 30-minute extreme must exceed the prior swing extreme by this many
    /// ticks (2). The "30-minute high" itself is the engine's rolling swing extreme.</summary>
    public int MinNewExtremeTicks { get; set; } = 2;

    // ----- E2: divergence -----

    /// <summary>E2 bar path: the bar printing the extreme closed within this fraction of its
    /// range toward the extreme ("top third" → 2/3).</summary>
    public double CloseInExtremeFraction { get; set; } = 2.0 / 3.0;

    // ----- E3: location -----

    /// <summary>E3: max ticks from the new extreme to an LOI (4).</summary>
    public int LocationProximityTicks { get; set; } = 4;

    // ----- E4: trigger -----

    /// <summary>E4: diagonal imbalance ratio (3:1).</summary>
    public double ImbalanceRatio { get; set; } = 3.0;

    /// <summary>E4: how close to the extreme the diagonal imbalance must print (2 ticks).</summary>
    public int ImbalanceProximityTicks { get; set; } = 2;

    // ----- entry / stop / targets -----

    /// <summary>Entry: limit this many ticks inside the extreme (2 → sell limit at H2 − 2).</summary>
    public int EntryOffsetTicks { get; set; } = 2;

    /// <summary>Entry: working limit lifetime before it is cancelled unfilled (2 min).</summary>
    public int EntryExpirySeconds { get; set; } = 120;

    /// <summary>Stop: this many ticks beyond the extreme against the trade (2 → stop at H2 + 2).</summary>
    public int StopOffsetTicks { get; set; } = 2;

    /// <summary>T1 at this multiple of R (1R), exiting <see cref="T1ExitFraction"/>.</summary>
    public double T1RMultiple { get; set; } = 1.0;

    /// <summary>Fraction exited at T1 (50%).</summary>
    public double T1ExitFraction { get; set; } = 0.5;

    /// <summary>After T1: stop moves to entry ∓ this offset. Rulebook says "stop to entry" (0).</summary>
    public int BreakevenOffsetTicks { get; set; } = 0;

    /// <summary>T2 cap in R (3R); the developing POC (the rulebook's "VWAP or developing POC" —
    /// VWAP is not computed in v1, documented) caps below it.</summary>
    public double T2RCap { get; set; } = 3.0;

    // ----- invalidation / context lifetime -----

    /// <summary>Invalidation bucket width (the rulebook's 10-second bucket).</summary>
    public int DeltaBucketSeconds { get; set; } = 10;

    /// <summary>Invalidation: a 10-second bucket with this much aggressor delta the adverse way,
    /// printing beyond the prior swing extreme, means real participants showed up (150).</summary>
    public long InvalidationDeltaContracts { get; set; } = 150;

    /// <summary>Context lifetime: drop an un-triggered divergence after this long. Not in the
    /// rulebook (it gives only the 2-minute entry cancel) — an engineering bound.</summary>
    public int ContextExpirySeconds { get; set; } = 120;
}
