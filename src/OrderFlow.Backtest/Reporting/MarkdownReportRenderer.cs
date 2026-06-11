using System.Globalization;
using System.Text;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest.Reporting;

/// <summary>
/// Renders a <see cref="BacktestReport"/> to the M6 markdown report: overall summary,
/// per-setup and per-setup/direction performance, the invalidation-vs-stop exit breakdown,
/// MAE/MFE distributions, and an equity-curve summary. Deterministic and invariant-culture
/// so the same backtest yields the same document; the full equity curve and per-trade rows
/// live in the CSV side-files.
/// </summary>
public static class MarkdownReportRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exit reasons shown as columns — None is omitted (it only appears on non-trades).</summary>
    private static readonly ExitReason[] ExitColumns =
    {
        ExitReason.Target, ExitReason.Stop, ExitReason.Invalidation,
        ExitReason.TimeStop, ExitReason.SessionEnd, ExitReason.Scratch,
    };

    public static string Render(BacktestReport report, ReportMeta meta)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(meta.Title).Append("\n\n");
        if (meta.Sources.Count > 0)
        {
            sb.Append("**Sources:** ").Append(string.Join(", ", meta.Sources)).Append("\n\n");
        }
        sb.Append("**Cost model:** $").Append(Money(meta.CommissionPerRoundTurn))
          .Append(" round-turn/contract · tick value $").Append(Money(meta.TickValue))
          .Append(". All P&L is net of commission.\n\n");

        var o = report.Overall;
        sb.Append("## Summary\n\n");
        if (o.Trades == 0)
        {
            sb.Append("**No trades** were taken (")
              .Append(o.Candidates).Append(" candidates: ")
              .Append(o.Blocked).Append(" blocked, ")
              .Append(o.Expired).Append(" expired, ")
              .Append(o.Cancelled).Append(" cancelled).\n\n");
            AppendBlockedByFilter(sb, report);
            return sb.ToString();
        }

        sb.Append("| Metric | Value |\n|---|---:|\n");
        Row(sb, "Candidates", o.Candidates.ToString(Inv));
        Row(sb, "Trades", o.Trades.ToString(Inv));
        Row(sb, "Wins / losses / scratches", $"{o.Wins} / {o.Losses} / {o.Scratches}");
        Row(sb, "Hit rate", Pct(o.HitRate));
        Row(sb, "Net P&L", "$" + Money(o.NetPnl));
        Row(sb, "Gross P&L", "$" + Money(o.GrossPnl));
        Row(sb, "Commission", "$" + Money(o.Commission));
        Row(sb, "Expectancy / trade", "$" + Money(o.Expectancy));
        Row(sb, "Avg win / avg loss", "$" + Money(o.AvgWin) + " / $" + Money(o.AvgLoss));
        Row(sb, "Profit factor", Pf(o.ProfitFactor));
        Row(sb, "Max drawdown", "$" + Money(report.Equity.MaxDrawdown));
        sb.Append('\n');

        AppendPerformanceTable(sb, "## Per-setup performance", report.PerSetup);
        AppendPerformanceTable(sb, "## Per setup / direction", report.PerSetupDirection);
        AppendExitBreakdown(sb, report.PerSetup);
        AppendDistributions(sb, report);
        AppendEquity(sb, report);
        AppendBlockedByFilter(sb, report);
        return sb.ToString();
    }

    private static void AppendPerformanceTable(StringBuilder sb, string heading, IReadOnlyList<SetupStats> rows)
    {
        sb.Append(heading).Append("\n\n");
        sb.Append("| Scope | Trades | Hit rate | Net | Expectancy | Profit factor | Avg win | Avg loss | Avg MAE | Avg MFE |\n");
        sb.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var s in rows)
        {
            sb.Append("| ").Append(Scope(s))
              .Append(" | ").Append(s.Trades.ToString(Inv))
              .Append(" | ").Append(Pct(s.HitRate))
              .Append(" | $").Append(Money(s.NetPnl))
              .Append(" | $").Append(Money(s.Expectancy))
              .Append(" | ").Append(Pf(s.ProfitFactor))
              .Append(" | $").Append(Money(s.AvgWin))
              .Append(" | $").Append(Money(s.AvgLoss))
              .Append(" | ").Append(s.AvgMaeTicks.ToString("F1", Inv))
              .Append(" | ").Append(s.AvgMfeTicks.ToString("F1", Inv))
              .Append(" |\n");
        }
        sb.Append('\n');
    }

    private static void AppendExitBreakdown(StringBuilder sb, IReadOnlyList<SetupStats> perSetup)
    {
        sb.Append("## Exit reasons (invalidation vs stop)\n\n");
        sb.Append("| Setup |");
        foreach (var e in ExitColumns)
        {
            sb.Append(' ').Append(e).Append(" |");
        }
        sb.Append("\n|---|").Append(string.Concat(Enumerable.Repeat("---:|", ExitColumns.Length))).Append('\n');
        foreach (var s in perSetup)
        {
            sb.Append("| ").Append(Scope(s)).Append(" |");
            foreach (var e in ExitColumns)
            {
                sb.Append(' ').Append(s.ExitReasons.GetValueOrDefault(e)).Append(" |");
            }
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    private static void AppendDistributions(StringBuilder sb, BacktestReport report)
    {
        sb.Append("## MAE / MFE distributions (ticks)\n\n");
        sb.Append("| Metric | n | min | p25 | median | p75 | p90 | max | mean |\n");
        sb.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        DistRow(sb, "MAE", report.Mae);
        DistRow(sb, "MFE", report.Mfe);
        sb.Append('\n');

        static void DistRow(StringBuilder sb, string name, DistributionSummary d)
        {
            sb.Append("| ").Append(name)
              .Append(" | ").Append(d.Count.ToString(Inv))
              .Append(" | ").Append(d.Min.ToString("F1", Inv))
              .Append(" | ").Append(d.P25.ToString("F1", Inv))
              .Append(" | ").Append(d.Median.ToString("F1", Inv))
              .Append(" | ").Append(d.P75.ToString("F1", Inv))
              .Append(" | ").Append(d.P90.ToString("F1", Inv))
              .Append(" | ").Append(d.Max.ToString("F1", Inv))
              .Append(" | ").Append(d.Mean.ToString("F2", Inv))
              .Append(" |\n");
        }
    }

    private static void AppendEquity(StringBuilder sb, BacktestReport report)
    {
        var eq = report.Equity;
        sb.Append("## Equity curve\n\n");
        sb.Append("Final equity **$").Append(Money(eq.FinalEquity))
          .Append("**, peak $").Append(Money(eq.Peak))
          .Append(", max drawdown $").Append(Money(eq.MaxDrawdown))
          .Append(" over ").Append(eq.Points.Count).Append(" trades. ")
          .Append("Full point-by-point curve in `equity_curve.csv`.\n\n");
    }

    private static void AppendBlockedByFilter(StringBuilder sb, BacktestReport report)
    {
        if (report.BlockedByFilter.Count == 0)
        {
            return;
        }
        sb.Append("## Blocked candidates (by risk filter)\n\n");
        sb.Append("| Filter | Count |\n|---|---:|\n");
        foreach (var (filter, count) in report.BlockedByFilter)
        {
            Row(sb, filter, count.ToString(Inv));
        }
        sb.Append('\n');
    }

    private static void Row(StringBuilder sb, string k, string v) =>
        sb.Append("| ").Append(k).Append(" | ").Append(v).Append(" |\n");

    private static string Scope(SetupStats s) =>
        s.Setup is null ? "ALL"
        : s.Direction is null ? s.Setup.ToString()!
        : $"{s.Setup} {s.Direction}";

    private static string Money(decimal v) => v.ToString("F2", Inv);

    private static string Pct(double ratio) => (ratio * 100.0).ToString("F1", Inv) + "%";

    private static string Pf(double? pf) => pf is null ? "—" : pf.Value.ToString("F2", Inv);
}
