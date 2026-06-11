using System.Diagnostics;
using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;
using OrderFlow.Infrastructure.Config;
using OrderFlow.Infrastructure.Dbn;
using OrderFlow.Infrastructure.Journal;
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
        bool withTrade = CliArgs.HasFlag(rest, "--trade");
        bool withFeatures = CliArgs.HasFlag(rest, "--features") || withTrade;
        FeatureEngineStage? features = null;
        TradingStage? trading = null;
        SqliteCandidateJournal? journal = null;
        string journalPath = CliArgs.GetOption(rest, "--journal") ?? path + ".journal.db";
        IBookEventObserver observer = stats;
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
        if (withTrade)
        {
            // The journal is a per-run derived artifact: start it fresh so a re-run of the
            // same file produces an identical database.
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            journal = new SqliteCandidateJournal(journalPath, options.Features);
            trading = new TradingStage(features!, (engine, nextCandidateId) =>
            {
                var simulator = new FillSimulator(tick, options.Execution);
                var coordinator = new TradingCoordinator(
                    tick, options.Detectors, options.Risk, options.Execution,
                    simulator, journal, engine, nextCandidateId);
                simulator.Listener = coordinator;
                return (coordinator, simulator);
            });
            // Order matters: features must ingest each event before detectors query them.
            observer = new CompositeBookEventObserver(stats, features!, trading);
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
        finally
        {
            journal?.Dispose();
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
        if (features is not null && CliArgs.HasFlag(rest, "--features"))
        {
            FeatureSnapshotPrinter.Print(Console.Out, features, options.Features);
            Console.WriteLine();
        }
        if (withTrade)
        {
            foreach (var (instrumentId, entry) in trading!.Instruments)
            {
                foreach (var detector in entry.Coordinator.Detectors)
                {
                    if (detector.FunnelLine() is { } line)
                    {
                        Console.WriteLine($"Funnel [{instrumentId} {detector.Setup} {detector.Direction}]: {line}");
                    }
                }
            }
            TradeSummaryPrinter.Print(Console.Out, journalPath);
            Console.WriteLine($"Journal:  {journalPath}");
            if (CliArgs.GetOption(rest, "--export-csv") is { } csvDir)
            {
                Directory.CreateDirectory(csvDir);
                JournalExporter.ExportCsv(journalPath, csvDir);
                Console.WriteLine($"CSV:      {csvDir}");
            }
            if (CliArgs.GetOption(rest, "--export-parquet") is { } parquetDir)
            {
                Directory.CreateDirectory(parquetDir);
                await JournalExporter.ExportParquetAsync(journalPath, parquetDir);
                Console.WriteLine($"Parquet:  {parquetDir}");
            }
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
