namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 1 (absorption fade) thresholds — rulebook A1–A6, entry, stop, targets and
/// invalidations, every number a named config value (appsettings "Detectors:Setup1").
/// Defaults are the rulebook's stated ES values. Stated for the long version; the short
/// mirror flips signs, not thresholds.
/// </summary>
public sealed class Setup1Options
{
    public bool Enabled { get; set; } = true;

    // ----- A1: context — decline into a level -----

    /// <summary>A1: minimum adverse move toward the level over the decline window (6 ticks).</summary>
    public int DeclineTicks { get; set; } = 6;

    /// <summary>A1: decline lookback ("the last 3 minutes").</summary>
    public int DeclineWindowSeconds { get; set; } = 180;

    /// <summary>A1: max distance from the LOI that defines the level L (2 ticks).</summary>
    public int ContextLoiProximityTicks { get; set; } = 2;

    // ----- A2: context — genuinely aggressive flow -----

    /// <summary>A2: flow window the delta is measured over (60 s).</summary>
    public int DeltaWindowSeconds { get; set; } = 60;

    /// <summary>A2: absolute windowed-delta threshold against the fade (300 contracts).</summary>
    public long DeltaContracts { get; set; } = 300;

    /// <summary>A2: alternative session-distribution tail (10th percentile).</summary>
    public double DeltaPercentile { get; set; } = 0.10;

    // ----- A3–A6: signal -----

    /// <summary>A3: minimum stall duration with the defended best holding the level (45 s).</summary>
    public int StallSeconds { get; set; } = 45;

    /// <summary>A3: "despite continued sell prints" quantifier — minimum aggressor prints
    /// during the stall. The rulebook gives no number; 1 = any continued selling.</summary>
    public int MinStallSellPrints { get; set; } = 1;

    /// <summary>A4: stall volume at [L, L+1] as a multiple of the 15-min per-price baseline (3×).</summary>
    public double StallVolumeMultiple { get; set; } = 3.0;

    /// <summary>A5: traded(L) ÷ max displayed(L) over the replenish window (2.5).</summary>
    public double ReplenishRatioMin { get; set; } = 2.5;

    /// <summary>A5: refresh events at L during the stall (3). MBP-10 approximation: any
    /// displayed-size increase at an already-populated L counts; the rulebook's "to ≥ 60%
    /// of pre-hit size" qualifier is not separately enforced.</summary>
    public int RefreshCountMin { get; set; } = 3;

    /// <summary>A6: exhaustion bucket width (10 s).</summary>
    public int ExhaustionBucketSeconds { get; set; } = 10;

    /// <summary>A6: required drop of the latest completed bucket from the stall's peak (70%).</summary>
    public double ExhaustionDropRatio { get; set; } = 0.70;

    /// <summary>Context reset when price runs this many ticks beyond L away from the fade —
    /// the absorption resolved without us. Engineering guard, not in the rulebook (one
    /// tick beyond the momentum-entry trigger).</summary>
    public int StallAbandonTicks { get; set; } = 4;

    // ----- entry -----

    /// <summary>Entry: buy limit at L + this offset (1 tick).</summary>
    public int EntryLimitOffsetTicks { get; set; } = 1;

    /// <summary>Entry: seconds the limit works before the momentum switch is considered (30 s).</summary>
    public int LimitWorkingSeconds { get; set; } = 30;

    /// <summary>Entry: minimum price advance beyond L for the momentum switch ("price ≥ L + 2").</summary>
    public int MomentumMinAdvanceTicks { get; set; } = 2;

    /// <summary>Entry: momentum buy stop at L + this offset (3 ticks).</summary>
    public int MomentumStopOffsetTicks { get; set; } = 3;

    /// <summary>Entry: total working time before the setup expires unfilled (90 s).</summary>
    public int EntryExpirySeconds { get; set; } = 90;

    // ----- stop / targets / management -----

    /// <summary>Hard stop at L − this offset (3 ticks). Never widened.</summary>
    public int StopOffsetTicks { get; set; } = 3;

    /// <summary>T1 at entry + this multiple of R (1R), exits <see cref="T1ExitFraction"/>.</summary>
    public double T1RMultiple { get; set; } = 1.0;

    /// <summary>Fraction of the position exited at T1 (50%).</summary>
    public double T1ExitFraction { get; set; } = 0.5;

    /// <summary>After T1: stop to entry − this offset (1 tick).</summary>
    public int BreakevenOffsetTicks { get; set; } = 1;

    /// <summary>T2 cap in R (3R); the opposing structure (developing POC) caps below it.</summary>
    public double T2RCap { get; set; } = 3.0;

    /// <summary>Time stop: exit at market when neither T1 nor stop is hit in this long (5 min).</summary>
    public int TimeStopSeconds { get; set; } = 300;

    /// <summary>Invalidation: a single print or one sweep of at least this size trading
    /// through L exits/cancels immediately (200 contracts).</summary>
    public long InvalidationSweepContracts { get; set; } = 200;

    /// <summary>Invalidation: the absorbing level is "pulled" when more than this fraction
    /// of its max displayed size (since arm) disappears without trading (80%).</summary>
    public double InvalidationVanishRatio { get; set; } = 0.8;
}
