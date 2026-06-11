using System.Globalization;
using System.Text;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest.Reporting;

/// <summary>
/// Writes the M6 report CSV side-files (CLAUDE.md: export to a markdown report + CSVs):
///   report_summary.csv — one row per scope (overall, per setup, per setup/direction);
///   equity_curve.csv   — cumulative net P&L per trade in chronological order;
///   trades.csv         — per-trade outcomes (the MAE/MFE distribution source data).
/// Invariant culture and fixed column order make the output deterministic.
/// </summary>
public static class ReportCsvWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(BacktestReport report, string outDir)
    {
        Directory.CreateDirectory(outDir);
        WriteSummary(report, Path.Combine(outDir, "report_summary.csv"));
        WriteEquity(report, Path.Combine(outDir, "equity_curve.csv"));
        WriteTrades(report, Path.Combine(outDir, "trades.csv"));
    }

    private static void WriteSummary(BacktestReport report, string path)
    {
        var sb = new StringBuilder();
        sb.Append("scope,setup,direction,candidates,blocked,expired,cancelled,trades,wins,losses,scratches,")
          .Append("hit_rate,gross_pnl,commission,net_pnl,expectancy,avg_win,avg_loss,profit_factor,avg_mae_ticks,avg_mfe_ticks\n");
        SummaryRow(sb, "overall", report.Overall);
        foreach (var s in report.PerSetup)
        {
            SummaryRow(sb, "setup", s);
        }
        foreach (var s in report.PerSetupDirection)
        {
            SummaryRow(sb, "setup_direction", s);
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void SummaryRow(StringBuilder sb, string scope, SetupStats s)
    {
        sb.Append(scope).Append(',')
          .Append(s.Setup?.ToString() ?? "ALL").Append(',')
          .Append(s.Direction?.ToString() ?? "ALL").Append(',')
          .Append(s.Candidates).Append(',')
          .Append(s.Blocked).Append(',')
          .Append(s.Expired).Append(',')
          .Append(s.Cancelled).Append(',')
          .Append(s.Trades).Append(',')
          .Append(s.Wins).Append(',')
          .Append(s.Losses).Append(',')
          .Append(s.Scratches).Append(',')
          .Append(s.HitRate.ToString("F4", Inv)).Append(',')
          .Append(Dec(s.GrossPnl)).Append(',')
          .Append(Dec(s.Commission)).Append(',')
          .Append(Dec(s.NetPnl)).Append(',')
          .Append(Dec(s.Expectancy)).Append(',')
          .Append(Dec(s.AvgWin)).Append(',')
          .Append(Dec(s.AvgLoss)).Append(',')
          .Append(s.ProfitFactor?.ToString("F4", Inv) ?? string.Empty).Append(',')
          .Append(s.AvgMaeTicks.ToString("F2", Inv)).Append(',')
          .Append(s.AvgMfeTicks.ToString("F2", Inv)).Append('\n');
    }

    private static void WriteEquity(BacktestReport report, string path)
    {
        var sb = new StringBuilder();
        sb.Append("index,candidate_id,cumulative_net\n");
        foreach (var p in report.Equity.Points)
        {
            sb.Append(p.Index).Append(',').Append(p.CandidateId).Append(',').Append(Dec(p.CumulativeNet)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteTrades(BacktestReport report, string path)
    {
        var sb = new StringBuilder();
        sb.Append("id,setup,direction,exit_reason,t1_filled,mae_ticks,mfe_ticks,gross_pnl,commission,net_pnl\n");
        foreach (var t in report.Trades)
        {
            sb.Append(t.Id).Append(',')
              .Append(t.Setup).Append(',')
              .Append(t.Direction).Append(',')
              .Append(t.ExitReason).Append(',')
              .Append(t.T1Filled ? 1 : 0).Append(',')
              .Append(t.MaeTicks?.ToString(Inv) ?? string.Empty).Append(',')
              .Append(t.MfeTicks?.ToString(Inv) ?? string.Empty).Append(',')
              .Append(t.GrossPnl is { } g ? Dec(g) : string.Empty).Append(',')
              .Append(t.Commission is { } c ? Dec(c) : string.Empty).Append(',')
              .Append(t.NetPnl is { } n ? Dec(n) : string.Empty).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string Dec(decimal v) => v.ToString("F2", Inv);
}
