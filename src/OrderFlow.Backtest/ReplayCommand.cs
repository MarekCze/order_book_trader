using System.Diagnostics;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Primitives;
using OrderFlow.Infrastructure.Config;
using OrderFlow.Infrastructure.Dbn;

namespace OrderFlow.Backtest;

internal static class ReplayCommand
{
    public static async Task<int> RunAsync(string path, string[] rest)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }
        var options = AppOptions.Load();
        decimal tickDecimal = CliArgs.GetDecimal(rest, "--tick-size") ?? options.Instrument.TickSize;
        var tick = TickSize.FromDecimal(tickDecimal);

        var source = new DbnFileMarketEventSource(path);
        var stage = new BookBuilderStage();
        var stats = new StatsCollector(tick);

        // Wall-clock time is allowed here ONLY for throughput measurement (CLI host);
        // all pipeline logic runs on event timestamps.
        var sw = Stopwatch.StartNew();
        try
        {
            await ReplayPipeline.RunAsync(source, stage, stats, options.Pipeline.ChannelCapacity);
        }
        catch (DbnFormatException ex)
        {
            Console.Error.WriteLine($"DBN format error: {ex.Message}");
            return 1;
        }
        sw.Stop();

        var md = source.Metadata!;
        Console.WriteLine($"File:     {path}");
        Console.WriteLine($"DBN:      v{md.Version}, dataset {md.Dataset}, schema {(md.IsMboSchema ? "mbo" : $"raw={md.RawSchema}")}, " +
                          $"symbols [{string.Join(", ", md.Symbols)}]");
        Console.WriteLine($"Window:   {md.Start} -> {md.End}");
        Console.WriteLine($"Tick:     {tick}");
        Console.WriteLine();
        stats.Print(Console.Out, stage);
        Console.WriteLine();

        long skippedTotal = 0;
        for (int rtype = 0; rtype < source.SkippedByRtype.Count; rtype++)
        {
            if (source.SkippedByRtype[rtype] > 0)
            {
                Console.WriteLine($"Skipped rtype 0x{rtype:X2}: {source.SkippedByRtype[rtype]:N0}");
                skippedTotal += source.SkippedByRtype[rtype];
            }
        }
        if (source.IgnoredMboActionCount > 0)
        {
            Console.WriteLine($"Ignored MBO 'N'/unknown actions: {source.IgnoredMboActionCount:N0}");
        }

        double seconds = sw.Elapsed.TotalSeconds;
        double rate = seconds > 0 ? source.EventsRead / seconds : 0;
        Console.WriteLine();
        Console.WriteLine($"Replayed {source.EventsRead:N0} events ({skippedTotal:N0} non-MBO records skipped) " +
                          $"in {seconds:F2}s = {rate:N0} events/s");
        return 0;
    }
}
