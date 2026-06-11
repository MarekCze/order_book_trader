using System.Globalization;
using Microsoft.Data.Sqlite;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;
using OrderFlow.Infrastructure.Config;
using OrderFlow.Infrastructure.Dbn;

namespace OrderFlow.Backtest;

/// <summary>
/// <c>orderflow inspect-trade &lt;journal.db&gt; &lt;candidate-id&gt;</c> — single-candidate audit
/// readout (tuning iteration 2, Setup-5 audit tooling). Prints the journaled row (header,
/// outcome, fills, full F1–F36 feature snapshot) and, for Setup 5, the E1–E4 evidence at
/// trigger. With <c>--data file.dbn[.zst]</c> it replays the file to reconstruct the
/// detector-internal context the journal does not carry (H1/H2 swing sample, E1–E4
/// verdicts, ±60 s price/delta path around the entry). Read-only over the journal.
/// </summary>
internal static class InspectTradeCommand
{
    private static readonly string[] HeaderColumns =
    {
        "id", "ts_ns", "session_date", "setup", "direction", "level_raw", "block", "quantity",
        "disposition", "exit_reason", "entry_ts_ns", "entry_price_raw", "entry_qty",
        "t1_filled", "mae_ticks", "mfe_ticks", "gross_pnl", "commission", "net_pnl",
    };

    public static async Task<int> RunAsync(string journalPath, string idArg, string[] rest)
    {
        if (!File.Exists(journalPath))
        {
            Console.Error.WriteLine($"Journal not found: {journalPath}");
            return 1;
        }
        if (!long.TryParse(idArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            Console.Error.WriteLine($"Not a candidate id: {idArg}");
            return 1;
        }
        AppOptions options;
        try
        {
            options = AppOptions.Load(basePath: null, overrides: CliArgs.GetAll(rest, "--set"));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        var tick = TickSize.FromDecimal(
            CliArgs.GetDecimal(rest, "--tick-size") ?? options.Instrument.TickSize);

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();

        var row = LoadCandidate(conn, id);
        if (row is null)
        {
            Console.Error.WriteLine($"Candidate {id} not found in {journalPath}");
            return 1;
        }
        var fills = LoadFills(conn, id);

        var w = Console.Out;
        PrintHeader(w, journalPath, row, fills, tick, options.Risk.TickValue);
        PrintFeatureSnapshot(w, row);

        long setup = (long)row["setup"]!;
        if (setup == (long)SetupId.DeltaDivergenceFade)
        {
            PrintSetup5JournalEvidence(w, row, options.Detectors.Setup5);
        }

        if (CliArgs.GetOption(rest, "--data") is { } dataPath)
        {
            if (!File.Exists(dataPath))
            {
                Console.Error.WriteLine($"Data file not found: {dataPath}");
                return 1;
            }
            if (setup != (long)SetupId.DeltaDivergenceFade)
            {
                w.WriteLine();
                w.WriteLine($"(--data reconstruction implemented for Setup 5 only; this is setup {setup}.)");
                return 0;
            }
            await ReconstructAsync(w, dataPath, row, tick, options);
        }
        else
        {
            w.WriteLine();
            w.WriteLine("(Pass --data <file.dbn[.zst]> to reconstruct E1/E4 context and the ±60s price path.)");
        }
        return 0;
    }

    // ----- journal loading -----

    internal static Dictionary<string, object?>? LoadCandidate(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM candidates WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++)
        {
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }
        return row;
    }

    private static List<(long Seq, string Role, long TsNs, string Side, long PriceRaw, long Qty)> LoadFills(
        SqliteConnection conn, long id)
    {
        var fills = new List<(long, string, long, string, long, long)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq, role, ts_ns, side, price_raw, qty FROM fills WHERE candidate_id = $id ORDER BY seq";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            fills.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
        }
        return fills;
    }

    // ----- printing -----

    private static void PrintHeader(
        TextWriter w,
        string journalPath,
        Dictionary<string, object?> row,
        List<(long Seq, string Role, long TsNs, string Side, long PriceRaw, long Qty)> fills,
        TickSize tick,
        decimal tickValue)
    {
        long setup = (long)row["setup"]!;
        var level = new Price((long)row["level_raw"]!);
        w.WriteLine($"Candidate {row["id"]} ({journalPath})");
        w.WriteLine($"  Trigger:  {Ts(row["ts_ns"])}  session {row["session_date"]}");
        w.WriteLine($"  Setup:    {setup} ({(SetupId)(byte)setup}) {row["direction"]}, level {level}");
        w.WriteLine($"  Risk:     block {row["block"]}, quantity {row["quantity"]}");
        w.WriteLine($"  Outcome:  {row["disposition"] ?? "(unresolved)"}" +
                    (row["exit_reason"] is { } er ? $", exit {er}" : "") +
                    (row["t1_filled"] is { } t1 ? $", T1 filled: {((long)t1 != 0 ? "yes" : "no")}" : ""));
        if (row["entry_ts_ns"] is { } ets)
        {
            var entryPx = new Price((long)row["entry_price_raw"]!);
            w.WriteLine($"  Entry:    {Ts(ets)} @ {entryPx} ×{row["entry_qty"]}");
            long lastExit = 0;
            foreach (var f in fills.Where(f => f.Role == "exit"))
            {
                w.WriteLine($"  Exit:     {Ts(f.TsNs)} @ {new Price(f.PriceRaw)} ×{f.Qty} ({f.Side})");
                lastExit = f.TsNs;
            }
            if (lastExit > 0)
            {
                double secs = (lastExit - (long)ets) / 1e9;
                w.WriteLine($"  Time in trade: {secs:F1}s");
            }
            w.WriteLine($"  MAE/MFE:  {row["mae_ticks"]} / {row["mfe_ticks"]} ticks " +
                        $"(tick {tick}, ${tickValue.ToString(CultureInfo.InvariantCulture)}/tick)");
            w.WriteLine($"  P&L:      gross {Money(row["gross_pnl"])}, commission {Money(row["commission"])}, " +
                        $"net {Money(row["net_pnl"])}");
        }
    }

    private static void PrintFeatureSnapshot(TextWriter w, Dictionary<string, object?> row)
    {
        w.WriteLine();
        w.WriteLine("Feature snapshot at trigger (journal row):");
        foreach (var (name, value) in row)
        {
            if (HeaderColumns.Contains(name))
            {
                continue;
            }
            string rendered = value switch
            {
                null => "null",
                double d => d.ToString("0.####", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            };
            w.WriteLine($"  {name,-28} {rendered}");
        }
    }

    private static void PrintSetup5JournalEvidence(TextWriter w, Dictionary<string, object?> row, Setup5Options o)
    {
        bool shortSide = (string)row["direction"]! == nameof(TradeDirection.Short);
        var level = new Price((long)row["level_raw"]!);
        w.WriteLine();
        w.WriteLine("Setup 5 — E1–E4 evidence in the journal (trigger-time features):");
        w.WriteLine($"  E1 (new 30-min extreme ≥ {o.MinNewExtremeTicks} ticks beyond prior swing):");
        w.WriteLine($"     H2 (the faded extreme) = level {level}; H1 (prior swing) is not journaled — needs --data.");
        w.WriteLine($"  E2 (cum-delta divergence): f9_cum_delta_div = {Val(row, "f9_cum_delta_div")}, " +
                    $"f25_bar_delta = {Val(row, "f25_bar_delta")}, f26_delta_price_div = {Val(row, "f26_delta_price_div")}");
        w.WriteLine($"  E3 (within {o.LocationProximityTicks} ticks of an LOI): " +
                    $"f6_loi_dist_ticks = {Val(row, "f6_loi_dist_ticks")} (type {Val(row, "f6_loi_type")}) " +
                    "— note: measured from last price at trigger, not from the extreme.");
        w.WriteLine($"  E4 (diagonal imbalance near extreme, or reclaim past prior swing): " +
                    $"f23_diag_imb_{(shortSide ? "sell" : "buy")} = {Val(row, shortSide ? "f23_diag_imb_sell" : "f23_diag_imb_buy")}, " +
                    $"f24_stacked_imb_len = {Val(row, "f24_stacked_imb_len")} — reclaim half needs --data.");
    }

    private static async Task ReconstructAsync(
        TextWriter w, string dataPath, Dictionary<string, object?> row, TickSize tick, AppOptions options)
    {
        bool highSide = (string)row["direction"]! == nameof(TradeDirection.Short);
        long triggerTs = (long)row["ts_ns"]!;
        long centerTs = row["entry_ts_ns"] is { } e ? (long)e : triggerTs;
        var o = options.Detectors.Setup5;
        var target = new TradeInspectionCollector.Target(
            TriggerTsNs: triggerTs,
            LevelRaw: (long)row["level_raw"]!,
            HighSide: highSide,
            CenterTsNs: centerTs,
            WindowSeconds: 60);
        var collector = new TradeInspectionCollector(tick, options.Features, o, target);

        var source = new DbnFileMarketEventSource(dataPath);
        await ReplayPipeline.RunAsync(source, new BookStateTrackerStage(), collector, options.Pipeline.ChannelCapacity);

        w.WriteLine();
        w.WriteLine($"Reconstruction from {Path.GetFileName(dataPath)} (ephemeral feature state — " +
                    "LOIs/profile lack prior-session history; the journaled f6 value is authoritative for E3):");
        w.WriteLine($"  Swing telemetry: confirmed swing highs {collector.ConfirmedSwingHighs:N0}, " +
                    $"lows {collector.ConfirmedSwingLows:N0}; fresh divergence samples " +
                    $"high {collector.DivergenceSamplesHigh:N0} / low {collector.DivergenceSamplesLow:N0} " +
                    $"(Features:SwingPullbackTicks={options.Features.SwingPullbackTicks})");

        if (collector.ContextSample is not { } d)
        {
            w.WriteLine("  No matching swing-divergence sample found for this level at/before the trigger.");
            w.WriteLine("  (Run with the same config and chained state the journal run used.)");
        }
        else
        {
            int sign = highSide ? -1 : 1;
            long extension = highSide
                ? d.NewExtreme.TicksFrom(d.PriorExtreme, tick)
                : d.PriorExtreme.TicksFrom(d.NewExtreme, tick);
            long cumDeltaGap = sign * (d.CumDeltaNew - d.CumDeltaPrior);
            bool barNonConfirming = sign * collector.ContextBarDelta >= 0;
            bool e1 = Setup5Guards.E1_NewExtremeBeyondPrior(extension, o);
            bool e2 = Setup5Guards.E2_Divergence(
                cumDeltaGap, barNonConfirming, collector.ContextDirectionalRangePos, o);
            bool e3 = Setup5Guards.E3_Location(collector.ContextLoiDistanceTicks, o);
            bool e4 = Setup5Guards.E4_Trigger(collector.E4ImbalanceAtTrigger, collector.E4ReclaimedAtTrigger);
            w.WriteLine($"  Context sample @ {d.Ts}:");
            w.WriteLine($"    H1 (prior swing) = {d.PriorExtreme} (cumDelta {d.CumDeltaPrior:N0})");
            w.WriteLine($"    H2 (new extreme) = {d.NewExtreme} (cumDelta {d.CumDeltaNew:N0})");
            w.WriteLine($"  E1: extension {extension} ticks beyond H1 (min {o.MinNewExtremeTicks}) → {Verdict(e1)}");
            w.WriteLine($"  E2: cumDelta gap {cumDeltaGap:N0} (>0 = diverging); bar delta {collector.ContextBarDelta:N0} " +
                        $"non-confirming={barNonConfirming}, close-toward-extreme {collector.ContextDirectionalRangePos:F2} " +
                        $"(≥{o.CloseInExtremeFraction:F2}) → {Verdict(e2)}");
            w.WriteLine($"  E3: LOI distance from H2 = {collector.ContextLoiDistanceTicks?.ToString("N0") ?? "none"} ticks " +
                        $"(type {collector.ContextLoiType?.ToString() ?? "—"}, max {o.LocationProximityTicks}) → {Verdict(e3)} " +
                        "(ephemeral-state caveat above)");
            w.WriteLine($"  E4 at trigger: imbalance-near-extreme={collector.E4ImbalanceAtTrigger}, " +
                        $"reclaimed-past-H1={collector.E4ReclaimedAtTrigger} → {Verdict(e4)}");
        }

        w.WriteLine();
        w.WriteLine($"Price/delta path ±60s around {(row["entry_ts_ns"] is null ? "trigger" : "entry")} ({Ts(centerTs)}), 10s buckets:");
        var buckets = PricePathSummary.Compute(collector.WindowTrades, centerTs, bucketSeconds: 10, windowSeconds: 60);
        if (buckets.Count == 0)
        {
            w.WriteLine("  no trades in window");
            return;
        }
        w.WriteLine("  bucket    trades     volume      delta       last       high        low");
        foreach (var b in buckets)
        {
            string label = $"{(b.Index >= 0 ? "+" : "")}{b.Index * 10}s";
            w.WriteLine($"  {label,-8} {b.Trades,7:N0} {b.Volume,10:N0} {b.Delta,10:N0} " +
                        $"{new Price(b.LastRaw),10} {new Price(b.HighRaw),10} {new Price(b.LowRaw),10}");
        }
    }

    private static string Verdict(bool pass) => pass ? "HOLDS" : "DOES NOT HOLD";

    private static string Val(Dictionary<string, object?> row, string col) =>
        row.TryGetValue(col, out var v) && v is not null
            ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "null"
            : "null";

    private static string Ts(object? tsNs) =>
        tsNs is null ? "—" : new Timestamp(unchecked((ulong)(long)tsNs)).ToString();

    private static string Money(object? v) =>
        v is null ? "—" : "$" + ((double)v).ToString("F2", CultureInfo.InvariantCulture);
}
