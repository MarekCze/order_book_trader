using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// F1–F15 feature snapshot at a decision time (rulebook Part 2, sections 2.1–2.2).
/// Null means "not computable here": one-sided book, ungated session distribution,
/// no trade yet, etc. Per-window arrays are indexed by
/// <see cref="FeatureEngineOptions.FlowWindowsSeconds"/> order; F2's array by
/// <see cref="FeatureEngineOptions.DepthImbalanceLevels"/> order. F7 (queue position)
/// is MBO-only and journaled as null in v1 — it has no slot here.
/// Allocated on demand at candidate events, not per market event.
/// </summary>
public sealed record FeatureSnapshot
{
    public required Timestamp Ts { get; init; }

    /// <summary>True when the snapshot time falls in the 17:00–18:00 ET break — features are stale.</summary>
    public required bool OutOfSession { get; init; }

    // 2.1 book state
    public required long? SpreadTicks { get; init; }                 // F1
    public required double?[] DepthImbalance { get; init; }          // F2, per k
    public required double? BookSlopeBid { get; init; }              // F3
    public required double? BookSlopeAsk { get; init; }              // F3
    public required double? BboSizeRatio { get; init; }              // F4
    public required double? DepthZ { get; init; }                    // F5
    public required long? LoiDistanceTicks { get; init; }            // F6
    public required LoiType? NearestLoiType { get; init; }           // F6

    // 2.2 flow
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
}
