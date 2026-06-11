using OrderFlow.Backtest.Reporting;
using OrderFlow.Infrastructure.Config;

namespace OrderFlow.Backtest;

/// <summary>
/// M6 backtest reporting CLI: read a candidate journal database, compute the per-setup
/// expectancy / hit-rate / MAE-MFE / exit-breakdown / equity-curve report, and write it
/// out as a markdown document plus CSV side-files. Pure read of an existing journal — the
/// same journal always produces the same report.
/// </summary>
internal static class ReportCommand
{
    public static int Run(string journalPath, string[] rest)
    {
        if (!File.Exists(journalPath))
        {
            Console.Error.WriteLine($"Journal not found: {journalPath}");
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
        string mdPath = CliArgs.GetOption(rest, "--out") ?? journalPath + ".report.md";
        string csvDir = CliArgs.GetOption(rest, "--csv-dir") ?? journalPath + ".report";
        string title = CliArgs.GetOption(rest, "--title") ?? $"{options.Instrument.Symbol} Backtest Report";

        var meta = new ReportMeta(
            Title: title,
            Sources: new[] { Path.GetFileName(journalPath) },
            CommissionPerRoundTurn: options.Execution.CommissionPerContractRoundTurn,
            TickValue: options.Risk.TickValue);

        Generate(journalPath, meta, mdPath, csvDir, Console.Out);
        return 0;
    }

    /// <summary>Shared by the standalone command and replay's --report flag.</summary>
    public static BacktestReport Generate(string journalPath, ReportMeta meta, string mdPath, string csvDir, TextWriter log)
    {
        var report = BacktestReport.Compute(ReportDataReader.Read(journalPath));
        File.WriteAllText(mdPath, MarkdownReportRenderer.Render(report, meta));
        ReportCsvWriter.Write(report, csvDir);

        var o = report.Overall;
        log.WriteLine(
            $"Report:   {o.Trades} trades, {o.Wins}W/{o.Losses}L, net ${o.NetPnl:F2}, " +
            $"expectancy ${o.Expectancy:F2}/trade, maxDD ${report.Equity.MaxDrawdown:F2}");
        log.WriteLine($"Markdown: {mdPath}");
        log.WriteLine($"CSVs:     {csvDir}");
        return report;
    }
}
