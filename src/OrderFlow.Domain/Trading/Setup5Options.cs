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

    // ----- flow-exhaustion gates (opt-in; both default OFF → byte-identical baseline) -----
    // Diagnosis (runs/entry-quality-diagnosis.md): S5 fades LIVE climaxes — it triggers while
    // with-the-move aggressor flow is still extreme, so entries fire into continuation. These two
    // independent, opt-in gates refuse to fade an un-exhausted push. See tasks/001-s5-flow-exhaustion-gate.md.

    /// <summary>Option A — extreme-flow gate. When true, the trigger is blocked while the
    /// with-the-move flow z-score (F8 over <see cref="FlowClimaxWindowSeconds"/>) is ≥
    /// <see cref="MaxTriggerFlowZ"/>. OFF by default.</summary>
    public bool FlowClimaxGateEnabled { get; set; } = false;

    /// <summary>Option A: block the fade while with-the-move flow z-score is at/above this (the
    /// Day-1 cascade fired into +13σ..+21σ; ~10 blocked it). Only used when the gate is enabled.</summary>
    public double MaxTriggerFlowZ { get; set; } = 10.0;

    /// <summary>Option A: which F8 flow window (seconds) the z-score is read from. Must be a
    /// configured Features:FlowWindowsSeconds value.</summary>
    public int FlowClimaxWindowSeconds { get; set; } = 10;

    /// <summary>Option B — deceleration gate. When true, the trigger requires the most recently
    /// completed with-the-move delta bucket to have dropped by ≥ <see cref="ExhaustionDropRatio"/>
    /// from the peak of the trailing <see cref="ExhaustionLookbackBuckets"/> buckets (Setup 1 A6
    /// analogue). OFF by default.</summary>
    public bool FlowDecelGateEnabled { get; set; } = false;

    /// <summary>Option B: required fractional drop of the last completed with-move bucket below the
    /// trailing peak (0.70 = "dropped ≥ 70% from peak", mirroring Setup 1 A6).</summary>
    public double ExhaustionDropRatio { get; set; } = 0.70;

    /// <summary>Option B: width of the with-the-move delta buckets (seconds).</summary>
    public int ExhaustionBucketSeconds { get; set; } = 10;

    /// <summary>Option B: how many trailing completed buckets define the "peak" the last bucket is
    /// compared against.</summary>
    public int ExhaustionLookbackBuckets { get; set; } = 6;

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
