using System.Globalization;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Features;

namespace OrderFlow.Backtest;

/// <summary>
/// Prints each instrument's final F1–F15 snapshot after a replay — a real-data sanity
/// surface for the feature engine until the journal (M4) exists. Invariant culture and
/// fixed ordering keep the output usable in determinism diffs.
/// </summary>
internal static class FeatureSnapshotPrinter
{
    public static void Print(TextWriter w, FeatureEngineStage features, FeatureEngineOptions opts)
    {
        foreach (var (instrumentId, _) in features.Engines.OrderBy(kv => kv.Key))
        {
            var s = features.Snapshot(instrumentId)!;
            w.WriteLine($"Features (instrument {instrumentId}, at {s.Ts}{(s.OutOfSession ? ", OUT-OF-SESSION" : string.Empty)}):");
            w.WriteLine($"  F1  spread_ticks       {Fmt(s.SpreadTicks)}");
            w.WriteLine($"  F2  depth_imbalance    {PerLevel(opts.DepthImbalanceLevels, s.DepthImbalance)}");
            w.WriteLine($"  F3  book_slope         bid {Fmt(s.BookSlopeBid)} / ask {Fmt(s.BookSlopeAsk)}");
            w.WriteLine($"  F4  bbo_size_ratio     {Fmt(s.BboSizeRatio)}");
            w.WriteLine($"  F5  depth_z            {Fmt(s.DepthZ)}");
            w.WriteLine($"  F6  loi_dist_ticks     {Fmt(s.LoiDistanceTicks)} ({(s.NearestLoiType?.ToString() ?? "n/a")})");
            w.WriteLine($"  F8  delta              {PerWindow(opts.FlowWindowsSeconds, s.Delta)}");
            w.WriteLine($"  F8  delta_z            {PerWindow(opts.FlowWindowsSeconds, s.DeltaZ)}");
            w.WriteLine($"  F9  cum_delta_div      {Fmt(s.CumDeltaDivergence)}");
            w.WriteLine($"  F10 trade_intensity    {PerWindow(opts.FlowWindowsSeconds, s.TradeIntensity)}");
            w.WriteLine($"  F10 trade_intensity_z  {PerWindow(opts.FlowWindowsSeconds, s.TradeIntensityZ)}");
            w.WriteLine($"  F11 aggressor_run      {s.AggressorRunLength} ({s.AggressorRunSide})");
            w.WriteLine($"  F12 large_prints       {PerWindow(opts.FlowWindowsSeconds, s.LargePrintCount)}");
            w.WriteLine($"  F13 sweep_flag         {PerWindow(opts.FlowWindowsSeconds, s.SweepFlag)}");
            w.WriteLine($"  F14 decay              sell {Fmt(s.SellDecay)} / buy {Fmt(s.BuyDecay)}");
            w.WriteLine($"  F15 vol_at_price_ratio {Fmt(s.VolAtPriceRatio)}");
        }
    }

    private static string Fmt(double? v) =>
        v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "n/a";

    private static string Fmt(long? v) =>
        v is { } l ? l.ToString(CultureInfo.InvariantCulture) : "n/a";

    private static string PerWindow<T>(int[] windows, IReadOnlyList<T> values)
    {
        var parts = new string[windows.Length];
        for (int i = 0; i < windows.Length; i++)
        {
            string text = values[i] switch
            {
                null => "n/a",
                double d => d.ToString("0.####", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v.ToString() ?? "n/a",
            };
            parts[i] = $"{windows[i]}s={text}";
        }
        return string.Join("  ", parts);
    }

    private static string PerLevel(int[] levels, IReadOnlyList<double?> values)
    {
        var parts = new string[levels.Length];
        for (int i = 0; i < levels.Length; i++)
        {
            parts[i] = $"k{levels[i]}={Fmt(values[i])}";
        }
        return string.Join("  ", parts);
    }
}
