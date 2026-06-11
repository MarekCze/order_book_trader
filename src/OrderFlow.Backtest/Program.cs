using System.Globalization;
using OrderFlow.Backtest;

return args switch
{
    ["replay", var file, .. var rest] => await ReplayCommand.RunAsync(file, rest),
    ["synth", var file, .. var rest] => SynthCommand.Run(file, rest),
    ["report", var db, .. var rest] => ReportCommand.Run(db, rest),
    ["inspect-trade", var db, var id, .. var rest] => await InspectTradeCommand.RunAsync(db, id, rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        """
        orderflow — Order Flow Trading Bot CLI (backtest host)

        Usage:
          orderflow replay <file.dbn[.zst]> --stats [--features] [--trade] [--tick-size 0.25]
                   [--journal out.db] [--export-csv dir] [--export-parquet dir] [--report dir]
              Stream a Databento DBN mbp-10 file through decoder → book state tracker and
              print event counts, session high/low, volume by aggressor side, trade count,
              spread range and throughput. --features also runs the feature engine
              (F1-F36) and prints each instrument's final snapshot. --trade additionally
              runs the detectors + fill simulator + risk filters and writes the candidate
              journal (default <file>.journal.db, recreated each run), printing a trading
              summary; --export-csv/--export-parquet export the journal tables; --report dir
              writes the M6 markdown report + CSVs (report.md + report/ inside dir).
              --set Key=Value (repeatable) overrides any config value for this run without
              editing appsettings.json, e.g. --set Detectors:Setup1:StopOffsetTicks=4
              --set Risk.AtrPercentileMax=0.99 (':' or '.' path separators; for parameter sweeps).

          orderflow report <journal.db> [--out report.md] [--csv-dir dir] [--title "..."] [--set K=V]
              Compute the M6 backtest report from an existing journal: per-setup expectancy
              net of costs, hit rate, MAE/MFE distributions, invalidation-vs-stop exit
              breakdown and equity curve. Writes a markdown report and CSV side-files
              (report_summary.csv, equity_curve.csv, trades.csv).

          orderflow inspect-trade <journal.db> <candidate-id> [--data <file.dbn[.zst]>] [--set K=V]
              Audit one journaled candidate: header/outcome/fills, the full F1-F36 feature
              snapshot at trigger, and (Setup 5) the E1-E4 evidence. With --data, replays
              the file to reconstruct the swing-divergence context (H1/H2, E1-E4 verdicts)
              and a ±60s price/delta path around the entry. Read-only over the journal.

          orderflow synth <out.dbn.zst> [--events N] [--seed S]
              Generate a deterministic synthetic MBP-10 file (default 1,000,000 events, seed 42).
        """);
    return 2;
}

namespace OrderFlow.Backtest
{
    internal static class CliArgs
    {
        public static string? GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        public static bool HasFlag(string[] args, string name) => args.Contains(name);

        /// <summary>All values following each occurrence of a repeatable option (e.g. --set).</summary>
        public static List<string> GetAll(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    values.Add(args[i + 1]);
                }
            }
            return values;
        }

        public static decimal? GetDecimal(string[] args, string name) =>
            GetOption(args, name) is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;

        public static long? GetLong(string[] args, string name) =>
            GetOption(args, name) is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
    }
}
