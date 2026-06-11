using Microsoft.Data.Sqlite;

namespace OrderFlow.Backtest;

/// <summary>
/// Post-replay trading summary read back from the journal database: per setup/direction
/// candidate funnel (blocked → expired/cancelled → traded), win/loss split, net P&L, and
/// breakdowns by risk filter and exit reason. Full reporting (expectancy, MAE/MFE
/// distributions, equity curve) is M6 — this is the sanity readout.
/// </summary>
internal static class TradeSummaryPrinter
{
    public static void Print(TextWriter w, string journalPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();

        w.WriteLine("Trading summary (per setup/direction):");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT setup, direction,
                       COUNT(*) AS candidates,
                       SUM(block != 'None') AS blocked,
                       SUM(disposition = 'Expired') AS expired,
                       SUM(disposition = 'CancelledInvalidated') AS cancelled,
                       SUM(disposition = 'Traded') AS traded,
                       SUM(disposition = 'Traded' AND net_pnl > 0) AS wins,
                       COALESCE(SUM(CASE WHEN disposition = 'Traded' THEN net_pnl END), 0) AS net
                FROM candidates GROUP BY setup, direction ORDER BY setup, direction
                """;
            using var r = cmd.ExecuteReader();
            bool any = false;
            while (r.Read())
            {
                any = true;
                w.WriteLine(
                    $"  setup {r.GetInt64(0)} {r.GetString(1),-5}: " +
                    $"{r.GetInt64(2),4} candidates, {r.GetInt64(3),4} blocked, " +
                    $"{r.GetInt64(4),3} expired, {r.GetInt64(5),3} cancelled, " +
                    $"{r.GetInt64(6),3} traded ({r.GetInt64(7)} wins), net ${r.GetDouble(8):F2}");
            }
            if (!any)
            {
                w.WriteLine("  no candidates");
                return;
            }
        }

        PrintBreakdown(w, conn, "Blocked by filter:",
            "SELECT block, COUNT(*) FROM candidates WHERE block != 'None' GROUP BY block ORDER BY block");
        PrintBreakdown(w, conn, "Exits by reason:",
            "SELECT exit_reason, COUNT(*) FROM candidates WHERE disposition = 'Traded' GROUP BY exit_reason ORDER BY exit_reason");
    }

    private static void PrintBreakdown(TextWriter w, SqliteConnection conn, string title, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        bool first = true;
        while (r.Read())
        {
            if (first)
            {
                w.WriteLine(title);
                first = false;
            }
            w.WriteLine($"  {r.GetString(0),-20} {r.GetInt64(1)}");
        }
    }
}
