using OrderFlow.Backtest.Reporting;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// M6 output rendering: the markdown report and the CSV side-files. Both are deterministic
/// (invariant culture, fixed column order) so re-running a backtest reproduces byte-identical
/// report artifacts, matching the determinism guarantee on the journal itself.
/// </summary>
public class ReportRendererTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ofrender").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ReportRow Traded(long id, SetupId setup, TradeDirection dir, ExitReason exit, decimal net, long mae, long mfe) => new()
    {
        Id = id,
        Setup = setup,
        Direction = dir,
        Block = "None",
        Disposition = CandidateDisposition.Traded,
        ExitReason = exit,
        T1Filled = exit == ExitReason.Target,
        MaeTicks = mae,
        MfeTicks = mfe,
        GrossPnl = net + 1.40m,
        Commission = 1.40m,
        NetPnl = net,
    };

    private static BacktestReport SampleReport() => BacktestReport.Compute(new[]
    {
        Traded(1, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Target, 100m, 2, 8),
        Traded(2, SetupId.AbsorptionFade, TradeDirection.Long, ExitReason.Stop, -50m, 6, 1),
        Traded(3, SetupId.StopRunFade, TradeDirection.Short, ExitReason.Invalidation, -20m, 4, 2),
    });

    private static ReportMeta Meta() => new(
        Title: "ES Backtest Report",
        Sources: new[] { "es-2026-06-01.dbn.zst" },
        CommissionPerRoundTurn: 1.40m,
        TickValue: 12.50m);

    [Fact]
    public void Markdown_ContainsHeadlineSectionsAndFigures()
    {
        string md = MarkdownReportRenderer.Render(SampleReport(), Meta());

        Assert.Contains("# ES Backtest Report", md);
        Assert.Contains("es-2026-06-01.dbn.zst", md);
        Assert.Contains("Expectancy", md);
        Assert.Contains("Hit rate", md);
        Assert.Contains("Equity curve", md);
        Assert.Contains("MAE", md);
        Assert.Contains("MFE", md);
        // Exit-reason breakdown must name the invalidation-vs-stop split.
        Assert.Contains("Invalidation", md);
        Assert.Contains("Stop", md);
        // Net overall = 100 - 50 - 20 = 30.
        Assert.Contains("30.00", md);
        // Per-setup row must mention the setup names.
        Assert.Contains("AbsorptionFade", md);
        Assert.Contains("StopRunFade", md);
    }

    [Fact]
    public void Markdown_EmptyReport_StillRenders()
    {
        string md = MarkdownReportRenderer.Render(BacktestReport.Compute(Array.Empty<ReportRow>()), Meta());
        Assert.Contains("# ES Backtest Report", md);
        Assert.Contains("No trades", md);
    }

    [Fact]
    public void Csv_WritesSummaryEquityAndTrades_WithHeaders()
    {
        ReportCsvWriter.Write(SampleReport(), _dir);

        string[] summary = File.ReadAllLines(Path.Combine(_dir, "report_summary.csv"));
        Assert.StartsWith("scope,setup,direction,candidates,blocked,expired,cancelled,trades,wins,losses,scratches,hit_rate,gross_pnl,commission,net_pnl,expectancy,avg_win,avg_loss,profit_factor,avg_mae_ticks,avg_mfe_ticks", summary[0]);
        // overall + 2 per-setup + 2 per-setup/direction = 5 data rows.
        Assert.Equal(6, summary.Length);

        string[] equity = File.ReadAllLines(Path.Combine(_dir, "equity_curve.csv"));
        Assert.Equal("index,candidate_id,cumulative_net", equity[0]);
        Assert.Equal(4, equity.Length); // header + 3 trades
        Assert.EndsWith(",30", equity[^1].Replace(".00", "")); // final cumulative = 30

        string[] trades = File.ReadAllLines(Path.Combine(_dir, "trades.csv"));
        Assert.Equal("id,setup,direction,exit_reason,t1_filled,mae_ticks,mfe_ticks,gross_pnl,commission,net_pnl", trades[0]);
        Assert.Equal(4, trades.Length); // header + 3 trades
    }

    [Fact]
    public void Csv_IsDeterministic()
    {
        var dir2 = Directory.CreateTempSubdirectory("ofrender2").FullName;
        try
        {
            ReportCsvWriter.Write(SampleReport(), _dir);
            ReportCsvWriter.Write(SampleReport(), dir2);
            foreach (var name in new[] { "report_summary.csv", "equity_curve.csv", "trades.csv" })
            {
                Assert.Equal(
                    File.ReadAllText(Path.Combine(_dir, name)),
                    File.ReadAllText(Path.Combine(dir2, name)));
            }
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }
}
