using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Full F1–F36 feature snapshot at a decision time (rulebook Part 2). This shape is the
/// future journal schema — treat it as a contract. Null means "not computable here":
/// one-sided book, ungated session distribution, no trade yet, missing history. F7, F19
/// and F20 are MBO-only and permanently null in v1 (kept as columns so the schema
/// doesn't change if MBO is adopted). Per-window arrays are indexed by
/// <see cref="FeatureEngineOptions.FlowWindowsSeconds"/> order; F2's array by
/// <see cref="FeatureEngineOptions.DepthImbalanceLevels"/> order.
/// Allocated on demand at candidate events, not per market event.
/// </summary>
public sealed record FeatureSnapshot
{
    public required Timestamp Ts { get; init; }

    /// <summary>True when the snapshot time falls in the 17:00–18:00 ET break — features are stale.</summary>
    public required bool OutOfSession { get; init; }

    // ----- 2.1 book state -----
    public required long? SpreadTicks { get; init; }                 // F1
    public required double?[] DepthImbalance { get; init; }          // F2, per k
    public required double? BookSlopeBid { get; init; }              // F3
    public required double? BookSlopeAsk { get; init; }              // F3
    public required double? BboSizeRatio { get; init; }              // F4
    public required double? DepthZ { get; init; }                    // F5
    public required long? LoiDistanceTicks { get; init; }            // F6
    public required LoiType? NearestLoiType { get; init; }           // F6
    public required double? QueuePositionEst { get; init; }          // F7 — MBO only, always null in v1

    // ----- 2.2 flow -----
    public required long[] Delta { get; init; }                      // F8 raw, per window
    public required double?[] DeltaZ { get; init; }                  // F8 z, per window
    public required double? CumDeltaDivergence { get; init; }        // F9
    public required double[] TradeIntensity { get; init; }           // F10 raw (trades/s), per window
    public required double?[] TradeIntensityZ { get; init; }         // F10 z, per window
    public required int AggressorRunLength { get; init; }            // F11
    public required Side AggressorRunSide { get; init; }             // F11
    public required long?[] LargePrintCount { get; init; }           // F12, per window
    public required bool[] SweepFlag { get; init; }                  // F13, per window
    public required double? SellDecay { get; init; }                 // F14
    public required double? BuyDecay { get; init; }                  // F14 mirror
    public required double? VolAtPriceRatio { get; init; }           // F15

    // ----- 2.3 liquidity dynamics -----
    public required double?[] PullRatioBid { get; init; }            // F16, per window
    public required double?[] PullRatioAsk { get; init; }            // F16, per window
    public required double? ReplenishRatioBid { get; init; }         // F17 at best bid
    public required double? ReplenishRatioAsk { get; init; }         // F17 at best ask
    public required int RefreshCountBid { get; init; }               // F18 at best bid
    public required int RefreshCountAsk { get; init; }               // F18 at best ask
    public required double? RefreshLatencyMs { get; init; }          // F19 — MBO only, always null in v1
    public required double? RefreshSizeCv { get; init; }             // F20 — MBO only, always null in v1
    public required double? DepthChangeBid { get; init; }            // F21
    public required double? DepthChangeAsk { get; init; }            // F21
    public required bool VanishFlagBid { get; init; }                // F22 at best bid
    public required bool VanishFlagAsk { get; init; }                // F22 at best ask

    // ----- 2.4 footprint (forming volume bar — leakage rule 2.8.2) -----
    public required int DiagImbalanceBuyCount { get; init; }         // F23
    public required int DiagImbalanceSellCount { get; init; }        // F23
    public required int StackedImbalanceLen { get; init; }           // F24
    public required long BarDelta { get; init; }                     // F25
    public required double? BarDeltaPctl { get; init; }              // F25
    public required bool DeltaPriceDivergence { get; init; }         // F26
    public required double? ExtremeVolumeShareHigh { get; init; }    // F27
    public required double? ExtremeVolumeShareLow { get; init; }     // F27
    public required bool UnfinishedAuctionHigh { get; init; }        // F28
    public required bool UnfinishedAuctionLow { get; init; }         // F28
    public required long? PocDriftTicks { get; init; }               // F29

    // ----- 2.5 profile / context -----
    public required long? DistPocTicks { get; init; }                // F30
    public required long? DistVahTicks { get; init; }                // F30
    public required long? DistValTicks { get; init; }                // F30
    public required long? DistNakedPocTicks { get; init; }           // F31
    public required double? LvnDepthRatio { get; init; }             // F32
    public required long? DistNextHvnUpTicks { get; init; }          // F33
    public required long? DistNextHvnDownTicks { get; init; }        // F33
    public required double? Atr5Points { get; init; }                // F34 raw
    public required double? Atr5Pctl { get; init; }                  // F34
    public required double? TodSin { get; init; }                    // F35
    public required double? TodCos { get; init; }                    // F35
    public required int? RthMinute { get; init; }                    // F35
    public required bool NewsWindowFlag { get; init; }               // F36
}
