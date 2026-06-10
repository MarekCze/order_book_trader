namespace OrderFlow.Domain.Features;

/// <summary>
/// Every M2 feature threshold, window and domain bound (CLAUDE.md: rulebook numbers are
/// config, never literals). Defaults are the rulebook's stated values. Bound from the
/// "Features" section of appsettings.json.
/// </summary>
public sealed class FeatureEngineOptions
{
    /// <summary>Rolling flow windows for F8/F10/F12/F13 (rulebook 2.2: w ∈ {10s, 30s, 60s, 300s}).</summary>
    public int[] FlowWindowsSeconds { get; set; } = { 10, 30, 60, 300 };

    /// <summary>Depth-imbalance level counts for F2 (rulebook 2.1: k ∈ {1, 3, 5, 10}).</summary>
    public int[] DepthImbalanceLevels { get; set; } = { 1, 3, 5, 10 };

    /// <summary>F5: trailing distribution window for the top-5 depth z-score (30 min).</summary>
    public int DepthZWindowSeconds { get; set; } = 1800;

    /// <summary>F9: swing-extreme lookback (E1's 30-minute extreme).</summary>
    public int SwingWindowSeconds { get; set; } = 1800;

    /// <summary>Session percentile baselines arm after this many live samples (CLAUDE.md: 200).</summary>
    public int SessionMinSamples { get; set; } = 200;

    /// <summary>F12: a print is large when its size ≥ this session quantile (rulebook: 95th).</summary>
    public double LargePrintPercentile { get; set; } = 0.95;

    /// <summary>F13: minimum distinct prices one aggression must consume to count as a sweep.</summary>
    public int SweepMinLevels { get; set; } = 2;

    /// <summary>F14: decay bucket width (A6's 10-second buckets).</summary>
    public int DecayBucketSeconds { get; set; } = 10;

    /// <summary>F14: lookback over which the peak bucket is found (proxy for "current stall").</summary>
    public int DecayLookbackSeconds { get; set; } = 300;

    /// <summary>F15: stall-window proxy (A3's 45-second stall).</summary>
    public int StallWindowSeconds { get; set; } = 45;

    /// <summary>F15: per-price baseline window (A4's prior 15 minutes).</summary>
    public int PerPriceBaselineSeconds { get; set; } = 900;

    /// <summary>F6: round-number LOI interval in points (25.00 for ES).</summary>
    public decimal RoundNumberIntervalPoints { get; set; } = 25.00m;

    /// <summary>F17/F18/F22: per-price lookback for max displayed, refreshes and traded volume (C1's 3 minutes).</summary>
    public int ReplenishWindowSeconds { get; set; } = 180;

    /// <summary>F22: untraded displayed-size drop (fraction of window max) that flags a vanish (rulebook: 80%).</summary>
    public double VanishDropRatio { get; set; } = 0.8;

    /// <summary>F21: number of levels beyond the best whose displayed size is tracked (B5: 3 levels).</summary>
    public int DepthChangeLevels { get; set; } = 3;

    /// <summary>F21: lag for the percentage depth change (D2's 30 seconds).</summary>
    public int DepthChangeLagSeconds { get; set; } = 30;

    /// <summary>F16: a displayed-size decrease counts as traded if covered by trade volume at that
    /// price within this many seconds (CLAUDE.md attribution heuristic).</summary>
    public int AttributionWindowSeconds { get; set; } = 1;

    /// <summary>Footprint bars are volume bars closing once this many contracts trade (rulebook 2.0).</summary>
    public int BarVolumeSize { get; set; } = 1000;

    /// <summary>F23/F24: diagonal imbalance ratio (rulebook: 3:1); the missing side floors at 1 contract.</summary>
    public double DiagonalImbalanceRatio { get; set; } = 3.0;

    /// <summary>F28: minimum two-sided volume at a bar extreme to call the auction unfinished.</summary>
    public int UnfinishedAuctionMinVolume { get; set; } = 5;

    /// <summary>F29: POC drift span — latest completed bar vs this many bars back (inclusive).</summary>
    public int PocDriftBars { get; set; } = 5;

    /// <summary>F25: completed bars needed before the bar-delta percentile answers (bars are scarce; 200 would gate most of a session).</summary>
    public int MinBarSamples { get; set; } = 30;

    /// <summary>Value area covers this fraction of session volume around the POC (rulebook: 70%).</summary>
    public double ValueAreaFraction { get; set; } = 0.70;

    /// <summary>LVN: local minimum below this fraction of session mean per-price volume (rulebook: 25%).</summary>
    public double LvnMeanRatio { get; set; } = 0.25;

    /// <summary>HVN (F33): local maximum at or above this multiple of session mean per-price volume.</summary>
    public double HvnMeanRatio { get; set; } = 1.5;

    /// <summary>F34: ATR bar width (rulebook: 5-minute ATR).</summary>
    public int AtrBarSeconds { get; set; } = 300;

    /// <summary>F34: number of true ranges averaged into the ATR.</summary>
    public int AtrPeriodBars { get; set; } = 14;

    /// <summary>F34: percentile lookback (rulebook: trailing 20-day distribution).</summary>
    public int AtrLookbackDays { get; set; } = 20;

    /// <summary>F34: distinct days of history required before the percentile answers.</summary>
    public int AtrMinDays { get; set; } = 5;

    /// <summary>F36: half-width of the news window (rulebook: ±10 minutes).</summary>
    public int NewsWindowMinutes { get; set; } = 10;

    /// <summary>F36: scheduled high-impact release times, ISO-8601 UTC. Empty by default —
    /// populate per backtest period (FOMC, CPI, NFP).</summary>
    public string[] NewsTimesUtc { get; set; } = Array.Empty<string>();

    /// <summary>Histogram domain half-range for windowed delta session distributions (contracts).</summary>
    public long DeltaHistogramRange { get; set; } = 50_000;

    /// <summary>Histogram domain caps for trade-count and trade-size session distributions.</summary>
    public long TradeCountHistogramMax { get; set; } = 50_000;

    public long TradeSizeHistogramMax { get; set; } = 10_000;
}
