using System.Globalization;
using OrderFlow.Backtest;

return args switch
{
    ["replay", var file, .. var rest] => await ReplayCommand.RunAsync(file, rest),
    ["synth", var file, .. var rest] => SynthCommand.Run(file, rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        """
        orderflow — Order Flow Trading Bot CLI (backtest host)

        Usage:
          orderflow replay <file.dbn[.zst]> --stats [--features] [--tick-size 0.25]
              Stream a Databento DBN mbp-10 file through decoder → book state tracker and
              print event counts, session high/low, volume by aggressor side, trade count,
              spread range and throughput. --features also runs the M2 feature engine
              (F1-F15) and prints each instrument's final snapshot.

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

        public static decimal? GetDecimal(string[] args, string name) =>
            GetOption(args, name) is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;

        public static long? GetLong(string[] args, string name) =>
            GetOption(args, name) is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
    }
}
