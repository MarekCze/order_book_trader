using Microsoft.Data.Sqlite;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Infrastructure.Journal;

/// <summary>
/// SQLite journal (CLAUDE.md: the journal is a first-class output and the future ML
/// training set — its schema is a contract). One row per candidate carrying the full
/// flattened F1–F36 snapshot at trigger time; outcome columns are NULL until the
/// candidate resolves; individual fills land in a child table. F7/F19/F20 are permanent
/// NULL columns (MBO-only) kept so the schema doesn't change if MBO is adopted.
/// Per-window features (F8, F10, F12, F13, F16) get one column per configured flow
/// window (default w10/w30/w60/w300); F2 one per configured depth level. All writes are
/// synchronous and in call order — replaying the same file yields an identical journal.
/// </summary>
public sealed class SqliteCandidateJournal : ICandidateJournal, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IReadOnlyList<FeatureColumn> _features;
    private readonly SqliteCommand _insertCandidate;
    private readonly SqliteCommand _updateOutcome;
    private readonly SqliteCommand _insertFill;

    internal sealed record FeatureColumn(string Name, string SqlType, Func<FeatureSnapshot, object?> Get);

    public SqliteCandidateJournal(string path, FeatureEngineOptions featureOptions)
    {
        _features = BuildFeatureColumns(featureOptions);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        CreateTables();
        _insertCandidate = PrepareInsertCandidate();
        _updateOutcome = PrepareUpdateOutcome();
        _insertFill = PrepareInsertFill();
    }

    public void RecordCandidate(CandidateRecord record)
    {
        var p = _insertCandidate.Parameters;
        p["$id"].Value = record.Id;
        p["$ts_ns"].Value = unchecked((long)record.Ts.UnixNanos);
        p["$session_date"].Value = record.SessionDate.ToString("yyyy-MM-dd");
        p["$setup"].Value = (long)record.Setup;
        p["$direction"].Value = record.Direction.ToString();
        p["$level_raw"].Value = record.Level.RawNano;
        p["$block"].Value = record.Block.ToString();
        p["$quantity"].Value = record.Quantity;
        p["$disposition"].Value = record.Block == RiskBlock.None
            ? DBNull.Value
            : CandidateDisposition.Blocked.ToString();
        for (int i = 0; i < _features.Count; i++)
        {
            p[$"$f{i}"].Value = _features[i].Get(record.Features) ?? DBNull.Value;
        }
        _insertCandidate.ExecuteNonQuery();
    }

    public void RecordOutcome(CandidateOutcome outcome)
    {
        var p = _updateOutcome.Parameters;
        p["$id"].Value = outcome.CandidateId;
        p["$disposition"].Value = outcome.Disposition.ToString();
        p["$exit_reason"].Value = outcome.ExitReason.ToString();
        p["$entry_ts_ns"].Value = outcome.EntryFill is { } entry ? unchecked((long)entry.Ts.UnixNanos) : DBNull.Value;
        p["$entry_price_raw"].Value = outcome.EntryFill is { } e2 ? e2.Price.RawNano : DBNull.Value;
        p["$entry_qty"].Value = outcome.EntryFill is { } e3 ? e3.Quantity : DBNull.Value;
        p["$t1_filled"].Value = outcome.T1Filled ? 1L : 0L;
        p["$mae_ticks"].Value = outcome.MaeTicks;
        p["$mfe_ticks"].Value = outcome.MfeTicks;
        p["$gross_pnl"].Value = (double)outcome.GrossPnl;
        p["$commission"].Value = (double)outcome.Commission;
        p["$net_pnl"].Value = (double)outcome.NetPnl;
        _updateOutcome.ExecuteNonQuery();

        int seq = 0;
        if (outcome.EntryFill is { } entryFill)
        {
            InsertFill(outcome.CandidateId, seq++, "entry", in entryFill);
        }
        foreach (var fill in outcome.ExitFills)
        {
            InsertFill(outcome.CandidateId, seq++, "exit", in fill);
        }
    }

    public void Dispose()
    {
        _insertCandidate.Dispose();
        _updateOutcome.Dispose();
        _insertFill.Dispose();
        _conn.Dispose();
    }

    private void InsertFill(long candidateId, int seq, string role, in Fill fill)
    {
        var p = _insertFill.Parameters;
        p["$candidate_id"].Value = candidateId;
        p["$seq"].Value = seq;
        p["$role"].Value = role;
        p["$ts_ns"].Value = unchecked((long)fill.Ts.UnixNanos);
        p["$side"].Value = fill.Side.ToString();
        p["$price_raw"].Value = fill.Price.RawNano;
        p["$qty"].Value = fill.Quantity;
        _insertFill.ExecuteNonQuery();
    }

    private void CreateTables()
    {
        var featureDdl = string.Join(",\n  ", _features.Select(c => $"{c.Name} {c.SqlType} NULL"));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"""
            CREATE TABLE IF NOT EXISTS candidates (
              id INTEGER PRIMARY KEY,
              ts_ns INTEGER NOT NULL,
              session_date TEXT NOT NULL,
              setup INTEGER NOT NULL,
              direction TEXT NOT NULL,
              level_raw INTEGER NOT NULL,
              block TEXT NOT NULL,
              quantity INTEGER NOT NULL,
              {featureDdl},
              disposition TEXT NULL,
              exit_reason TEXT NULL,
              entry_ts_ns INTEGER NULL,
              entry_price_raw INTEGER NULL,
              entry_qty INTEGER NULL,
              t1_filled INTEGER NULL,
              mae_ticks INTEGER NULL,
              mfe_ticks INTEGER NULL,
              gross_pnl REAL NULL,
              commission REAL NULL,
              net_pnl REAL NULL
            );
            CREATE TABLE IF NOT EXISTS fills (
              candidate_id INTEGER NOT NULL,
              seq INTEGER NOT NULL,
              role TEXT NOT NULL,
              ts_ns INTEGER NOT NULL,
              side TEXT NOT NULL,
              price_raw INTEGER NOT NULL,
              qty INTEGER NOT NULL,
              PRIMARY KEY (candidate_id, seq)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand PrepareInsertCandidate()
    {
        var names = new List<string> { "id", "ts_ns", "session_date", "setup", "direction", "level_raw", "block", "quantity", "disposition" };
        var values = new List<string> { "$id", "$ts_ns", "$session_date", "$setup", "$direction", "$level_raw", "$block", "$quantity", "$disposition" };
        for (int i = 0; i < _features.Count; i++)
        {
            names.Add(_features[i].Name);
            values.Add($"$f{i}");
        }
        var cmd = _conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO candidates ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})";
        foreach (var v in values)
        {
            cmd.Parameters.Add(new SqliteParameter(v, null));
        }
        return cmd;
    }

    private SqliteCommand PrepareUpdateOutcome()
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE candidates SET
              disposition = $disposition, exit_reason = $exit_reason,
              entry_ts_ns = $entry_ts_ns, entry_price_raw = $entry_price_raw, entry_qty = $entry_qty,
              t1_filled = $t1_filled, mae_ticks = $mae_ticks, mfe_ticks = $mfe_ticks,
              gross_pnl = $gross_pnl, commission = $commission, net_pnl = $net_pnl
            WHERE id = $id
            """;
        foreach (var name in new[]
                 {
                     "$id", "$disposition", "$exit_reason", "$entry_ts_ns", "$entry_price_raw", "$entry_qty",
                     "$t1_filled", "$mae_ticks", "$mfe_ticks", "$gross_pnl", "$commission", "$net_pnl",
                 })
        {
            cmd.Parameters.Add(new SqliteParameter(name, null));
        }
        return cmd;
    }

    private SqliteCommand PrepareInsertFill()
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO fills (candidate_id, seq, role, ts_ns, side, price_raw, qty) " +
            "VALUES ($candidate_id, $seq, $role, $ts_ns, $side, $price_raw, $qty)";
        foreach (var name in new[] { "$candidate_id", "$seq", "$role", "$ts_ns", "$side", "$price_raw", "$qty" })
        {
            cmd.Parameters.Add(new SqliteParameter(name, null));
        }
        return cmd;
    }

    /// <summary>The F1–F36 → column mapping. THIS LIST IS THE JOURNAL CONTRACT.</summary>
    internal static IReadOnlyList<FeatureColumn> BuildFeatureColumns(FeatureEngineOptions o)
    {
        var cols = new List<FeatureColumn>
        {
            new("out_of_session", "INTEGER", s => Flag(s.OutOfSession)),
            new("f1_spread_ticks", "INTEGER", s => s.SpreadTicks),
        };
        for (int i = 0; i < o.DepthImbalanceLevels.Length; i++)
        {
            int idx = i;
            cols.Add(new($"f2_depth_imb_k{o.DepthImbalanceLevels[i]}", "REAL", s => s.DepthImbalance[idx]));
        }
        cols.Add(new("f3_book_slope_bid", "REAL", s => s.BookSlopeBid));
        cols.Add(new("f3_book_slope_ask", "REAL", s => s.BookSlopeAsk));
        cols.Add(new("f4_bbo_size_ratio", "REAL", s => s.BboSizeRatio));
        cols.Add(new("f5_depth_z", "REAL", s => s.DepthZ));
        cols.Add(new("f6_loi_dist_ticks", "INTEGER", s => s.LoiDistanceTicks));
        cols.Add(new("f6_loi_type", "TEXT", s => s.NearestLoiType?.ToString()));
        cols.Add(new("f7_queue_position_est", "REAL", s => s.QueuePositionEst)); // MBO only — always NULL in v1
        for (int i = 0; i < o.FlowWindowsSeconds.Length; i++)
        {
            int idx = i;
            int w = o.FlowWindowsSeconds[i];
            cols.Add(new($"f8_delta_w{w}", "INTEGER", s => s.Delta[idx]));
            cols.Add(new($"f8_delta_z_w{w}", "REAL", s => s.DeltaZ[idx]));
            cols.Add(new($"f10_intensity_w{w}", "REAL", s => s.TradeIntensity[idx]));
            cols.Add(new($"f10_intensity_z_w{w}", "REAL", s => s.TradeIntensityZ[idx]));
            cols.Add(new($"f12_large_prints_w{w}", "INTEGER", s => s.LargePrintCount[idx]));
            cols.Add(new($"f13_sweep_w{w}", "INTEGER", s => Flag(s.SweepFlag[idx])));
            cols.Add(new($"f16_pull_ratio_bid_w{w}", "REAL", s => s.PullRatioBid[idx]));
            cols.Add(new($"f16_pull_ratio_ask_w{w}", "REAL", s => s.PullRatioAsk[idx]));
        }
        cols.Add(new("f9_cum_delta_div", "REAL", s => s.CumDeltaDivergence));
        cols.Add(new("f11_run_len", "INTEGER", s => (long)s.AggressorRunLength));
        cols.Add(new("f11_run_side", "TEXT", s => s.AggressorRunSide.ToString()));
        cols.Add(new("f14_sell_decay", "REAL", s => s.SellDecay));
        cols.Add(new("f14_buy_decay", "REAL", s => s.BuyDecay));
        cols.Add(new("f15_vol_at_price_ratio", "REAL", s => s.VolAtPriceRatio));
        cols.Add(new("f17_replenish_bid", "REAL", s => s.ReplenishRatioBid));
        cols.Add(new("f17_replenish_ask", "REAL", s => s.ReplenishRatioAsk));
        cols.Add(new("f18_refresh_bid", "INTEGER", s => (long)s.RefreshCountBid));
        cols.Add(new("f18_refresh_ask", "INTEGER", s => (long)s.RefreshCountAsk));
        cols.Add(new("f19_refresh_latency_ms", "REAL", s => s.RefreshLatencyMs)); // MBO only — always NULL in v1
        cols.Add(new("f20_refresh_size_cv", "REAL", s => s.RefreshSizeCv));       // MBO only — always NULL in v1
        cols.Add(new("f21_depth_chg_bid", "REAL", s => s.DepthChangeBid));
        cols.Add(new("f21_depth_chg_ask", "REAL", s => s.DepthChangeAsk));
        cols.Add(new("f22_vanish_bid", "INTEGER", s => Flag(s.VanishFlagBid)));
        cols.Add(new("f22_vanish_ask", "INTEGER", s => Flag(s.VanishFlagAsk)));
        cols.Add(new("f23_diag_imb_buy", "INTEGER", s => (long)s.DiagImbalanceBuyCount));
        cols.Add(new("f23_diag_imb_sell", "INTEGER", s => (long)s.DiagImbalanceSellCount));
        cols.Add(new("f24_stacked_imb_len", "INTEGER", s => (long)s.StackedImbalanceLen));
        cols.Add(new("f25_bar_delta", "INTEGER", s => s.BarDelta));
        cols.Add(new("f25_bar_delta_pctl", "REAL", s => s.BarDeltaPctl));
        cols.Add(new("f26_delta_price_div", "INTEGER", s => Flag(s.DeltaPriceDivergence)));
        cols.Add(new("f27_extreme_vol_high", "REAL", s => s.ExtremeVolumeShareHigh));
        cols.Add(new("f27_extreme_vol_low", "REAL", s => s.ExtremeVolumeShareLow));
        cols.Add(new("f28_unfinished_high", "INTEGER", s => Flag(s.UnfinishedAuctionHigh)));
        cols.Add(new("f28_unfinished_low", "INTEGER", s => Flag(s.UnfinishedAuctionLow)));
        cols.Add(new("f29_poc_drift_ticks", "INTEGER", s => s.PocDriftTicks));
        cols.Add(new("f30_dist_poc", "INTEGER", s => s.DistPocTicks));
        cols.Add(new("f30_dist_vah", "INTEGER", s => s.DistVahTicks));
        cols.Add(new("f30_dist_val", "INTEGER", s => s.DistValTicks));
        cols.Add(new("f31_dist_naked_poc", "INTEGER", s => s.DistNakedPocTicks));
        cols.Add(new("f32_lvn_depth_ratio", "REAL", s => s.LvnDepthRatio));
        cols.Add(new("f33_dist_hvn_up", "INTEGER", s => s.DistNextHvnUpTicks));
        cols.Add(new("f33_dist_hvn_down", "INTEGER", s => s.DistNextHvnDownTicks));
        cols.Add(new("f34_atr5_points", "REAL", s => s.Atr5Points));
        cols.Add(new("f34_atr5_pctl", "REAL", s => s.Atr5Pctl));
        cols.Add(new("f35_tod_sin", "REAL", s => s.TodSin));
        cols.Add(new("f35_tod_cos", "REAL", s => s.TodCos));
        cols.Add(new("f35_rth_minute", "INTEGER", s => s.RthMinute is { } m ? (long)m : null));
        cols.Add(new("f36_news_flag", "INTEGER", s => Flag(s.NewsWindowFlag)));
        return cols;

        static object Flag(bool b) => b ? 1L : 0L;
    }
}
