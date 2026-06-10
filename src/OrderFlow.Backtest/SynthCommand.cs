using System.Diagnostics;
using OrderFlow.Infrastructure.Dbn;

namespace OrderFlow.Backtest;

internal static class SynthCommand
{
    public static int Run(string path, string[] rest)
    {
        long events = CliArgs.GetLong(rest, "--events") ?? 1_000_000;
        int seed = (int)(CliArgs.GetLong(rest, "--seed") ?? 42);

        var generator = new SyntheticMbp10Generator(seed);
        var sw = Stopwatch.StartNew();
        using (var file = File.Create(path))
        using (var writer = new DbnMbp10Writer(file, new DbnWriterOptions
        {
            ZstdCompress = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase),
            StartNs = 1_767_621_600_000_000_000UL,
        }))
        {
            foreach (var e in generator.Generate(events))
            {
                writer.WriteEvent(in e);
            }
        }
        sw.Stop();
        Console.WriteLine($"Wrote {events:N0} synthetic MBP-10 events (seed {seed}) to {path} " +
                          $"({new FileInfo(path).Length:N0} bytes) in {sw.Elapsed.TotalSeconds:F2}s");
        return 0;
    }
}
