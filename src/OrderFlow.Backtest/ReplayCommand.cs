using System.Diagnostics;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Primitives;
using OrderFlow.Infrastructure.Config;
using OrderFlow.Infrastructure.Dbn;
using OrderFlow.Infrastructure.Storage;

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
        var stage = new BookStateTrackerStage();
        var stats = new StatsCollector(tick);
        FeatureEngineStage? features = null;
        IBookEventObserver observer = stats;
        bool withFeatures = CliArgs.HasFlag(rest, "--features");
        // Empty SqlitePath = ephemeral in-memory state, keeping repeated replays of the
        // same file byte-identical.
        using var store = withFeatures && !string.IsNullOrWhiteSpace(options.Storage.SqlitePath)
            ? new SqliteFeatureStateStore(options.Storage.SqlitePath)
            : null;
        if (withFeatures)
        {
            features = new FeatureEngineStage(tick, options.Features, nakedPocStore: store, atrHistoryStore: store);
            observer = new CompositeBookEventObserver(stats, features);
        }

        // Wall-clock time is allowed here ONLY for throughput measurement (CLI host);
        // all pipeline logic runs on event timestamps.
        var sw = Stopwatch.StartNew();
        try
        {
            await ReplayPipeline.RunAsync(source, stage, observer, options.Pipeline.ChannelCapacity);
        }
        catch (DbnFormatException ex)
        {
            Console.Error.WriteLine($"DBN format error: {ex.Message}");
            return 1;
        }
        sw.Stop();

        var md = source.Metadata!;
        Console.WriteLine($"File:     {path}");
        Console.WriteLine($"DBN:      v{md.Version}, dataset {md.Dataset}, schema {(md.IsMbp10Schema ? "mbp-10" : $"raw={md.RawSchema}")}, " +
                          $"symbols [{string.Join(", ", md.Symbols)}]");
        Console.WriteLine($"Window:   {md.Start} -> {md.End}");
        Console.WriteLine($"Tick:     {tick}");
        Console.WriteLine();
        stats.Print(Console.Out, stage);
        Console.WriteLine();
        if (features is not null)
        {
            FeatureSnapshotPrinter.Print(Console.Out, features, options.Features);
            Console.WriteLine();
        }

        long skippedTotal = 0;
        for (int rtype = 0; rtype < source.SkippedByRtype.Count; rtype++)
        {
            if (source.SkippedByRtype[rtype] > 0)
            {
                Console.WriteLine($"Skipped rtype 0x{rtype:X2}: {source.SkippedByRtype[rtype]:N0}");
                skippedTotal += source.SkippedByRtype[rtype];
            }
        }
        if (source.IgnoredActionCount > 0)
        {
            Console.WriteLine($"Ignored 'N'/'F'/unknown actions: {source.IgnoredActionCount:N0}");
        }

        double seconds = sw.Elapsed.TotalSeconds;
        double rate = seconds > 0 ? source.EventsRead / seconds : 0;
        Console.WriteLine();
        Console.WriteLine($"Replayed {source.EventsRead:N0} events ({skippedTotal:N0} non-MBP-10 records skipped) " +
                          $"in {seconds:F2}s = {rate:N0} events/s");
        return 0;
    }
}
