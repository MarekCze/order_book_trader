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

    /// <summary>Histogram domain half-range for windowed delta session distributions (contracts).</summary>
    public long DeltaHistogramRange { get; set; } = 50_000;

    /// <summary>Histogram domain caps for trade-count and trade-size session distributions.</summary>
    public long TradeCountHistogramMax { get; set; } = 50_000;

    public long TradeSizeHistogramMax { get; set; } = 10_000;
}
