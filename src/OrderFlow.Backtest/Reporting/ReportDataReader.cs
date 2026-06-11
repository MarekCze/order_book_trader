using Microsoft.Data.Sqlite;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest.Reporting;

/// <summary>
/// Reads the journal's <c>candidates</c> table into the pure <see cref="ReportRow"/> shape
/// the reporting engine consumes. Only the columns reporting needs are selected (the full
/// F1–F36 snapshot stays in the journal/CSV/Parquet exports). Rows come back ordered by
/// id — i.e. chronologically — so the equity curve is deterministic.
/// </summary>
public static class ReportDataReader
{
    public static IReadOnlyList<ReportRow> Read(string journalPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, setup, direction, block, disposition, exit_reason,
                   t1_filled, mae_ticks, mfe_ticks, gross_pnl, commission, net_pnl
            FROM candidates ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<ReportRow>();
        while (r.Read())
        {
            rows.Add(new ReportRow
            {
                Id = r.GetInt64(0),
                Setup = (SetupId)r.GetInt64(1),
                Direction = Enum.Parse<TradeDirection>(r.GetString(2)),
                Block = r.GetString(3),
                Disposition = r.IsDBNull(4) ? null : Enum.Parse<CandidateDisposition>(r.GetString(4)),
                ExitReason = r.IsDBNull(5) ? ExitReason.None : Enum.Parse<ExitReason>(r.GetString(5)),
                T1Filled = !r.IsDBNull(6) && r.GetInt64(6) != 0,
                MaeTicks = r.IsDBNull(7) ? null : r.GetInt64(7),
                MfeTicks = r.IsDBNull(8) ? null : r.GetInt64(8),
                GrossPnl = r.IsDBNull(9) ? null : (decimal)r.GetDouble(9),
                Commission = r.IsDBNull(10) ? null : (decimal)r.GetDouble(10),
                NetPnl = r.IsDBNull(11) ? null : (decimal)r.GetDouble(11),
            });
        }
        return rows;
    }
}
