using Microsoft.Data.Sqlite;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;
using OrderFlow.Infrastructure.Journal;

namespace OrderFlow.Tests;

/// <summary>
/// Journal persistence (CLAUDE.md: first-class output, schema = ML-training contract).
/// One SQLite row per candidate with the full flattened F1–F36 snapshot; outcome columns
/// updated when the candidate resolves; fills in a child table. CSV and Parquet exports
/// read back from the database.
/// </summary>
public class CandidateJournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ofjournal").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DbPath => Path.Combine(_dir, "journal.db");

    /// <summary>A realistic snapshot from a real engine fed three events (hand-building
    /// 50+ required init properties would just duplicate the engine).</summary>
    private static FeatureSnapshot MakeSnapshot()
    {
        ulong ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;
        var tracker = new BookStateTracker();
        var engine = new FeatureEngine(TickSize.Es, new FeatureEngineOptions());
        var levels = TestEvents.Levels(
            new (decimal?, uint, uint)[] { (5000.00m, 10, 2) },
            new (decimal?, uint, uint)[] { (5000.25m, 12, 3) });
        var e1 = TestEvents.Trade(Side.Ask, 5000.00m, 7, levels, ts);
        tracker.Apply(in e1);
        engine.OnEvent(in e1, tracker);
        var e2 = TestEvents.Trade(Side.Bid, 5000.25m, 4, levels, ts + 2_000_000_000);
        tracker.Apply(in e2);
        engine.OnEvent(in e2, tracker);
        return engine.ComputeSnapshot(tracker);
    }

    private static CandidateRecord MakeCandidate(long id = 1, RiskBlock block = RiskBlock.None) => new()
    {
        Id = id,
        Ts = new Timestamp(1_780_000_000_000_000_000),
        SessionDate = new DateOnly(2026, 6, 1),
        Setup = SetupId.AbsorptionFade,
        Direction = TradeDirection.Long,
        Level = Price.FromDecimal(5000.00m),
        Block = block,
        Quantity = block == RiskBlock.None ? 10 : 0,
        Features = MakeSnapshot(),
    };

    private static CandidateOutcome MakeOutcome(long candidateId = 1) => new()
    {
        CandidateId = candidateId,
        Disposition = CandidateDisposition.Traded,
        ExitReason = ExitReason.Stop,
        EntryFill = new Fill(7, new Timestamp(1_780_000_000_500_000_000), Side.Bid, Price.FromDecimal(5000.25m), 10),
        ExitFills = new[]
        {
            new Fill(8, new Timestamp(1_780_000_001_000_000_000), Side.Ask, Price.FromDecimal(4999.00m), 10),
        },
        T1Filled = false,
        MaeTicks = 5,
        MfeTicks = 2,
        GrossPnl = -625m,
        Commission = 14m,
        NetPnl = -639m,
    };

    [Fact]
    public void RoundTrips_Candidate_Outcome_AndFills()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(MakeCandidate());
            journal.RecordOutcome(MakeOutcome());
        }

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT setup, direction, block, quantity, disposition, exit_reason, net_pnl, " +
            "f1_spread_ticks, f8_delta_w60, f36_news_flag, entry_price_raw, mae_ticks FROM candidates";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((long)SetupId.AbsorptionFade, reader.GetInt64(0));
        Assert.Equal("Long", reader.GetString(1));
        Assert.Equal("None", reader.GetString(2));
        Assert.Equal(10, reader.GetInt64(3));
        Assert.Equal("Traded", reader.GetString(4));
        Assert.Equal("Stop", reader.GetString(5));
        Assert.Equal(-639.0, reader.GetDouble(6));
        Assert.Equal(1, reader.GetInt64(7));      // spread 1 tick in the test book
        Assert.Equal(-3, reader.GetInt64(8));     // delta(60s) = −7 + 4
        Assert.Equal(0, reader.GetInt64(9));      // no news configured
        Assert.Equal(Price.FromDecimal(5000.25m).RawNano, reader.GetInt64(10));
        Assert.Equal(5, reader.GetInt64(11));
        Assert.False(reader.Read());

        using var fillsCmd = conn.CreateCommand();
        fillsCmd.CommandText = "SELECT role, price_raw, qty FROM fills ORDER BY seq";
        using var fills = fillsCmd.ExecuteReader();
        Assert.True(fills.Read());
        Assert.Equal("entry", fills.GetString(0));
        Assert.True(fills.Read());
        Assert.Equal("exit", fills.GetString(0));
        Assert.Equal(Price.FromDecimal(4999.00m).RawNano, fills.GetInt64(1));
        Assert.Equal(10, fills.GetInt64(2));
        Assert.False(fills.Read());
    }

    [Fact]
    public void BlockedCandidate_PersistsWithoutOutcome()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(MakeCandidate(block: RiskBlock.Volatility));
        }
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT block, disposition FROM candidates";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Volatility", reader.GetString(0));
        Assert.Equal("Blocked", reader.GetString(1));
    }

    [Fact]
    public void CsvExport_WritesCandidatesAndFills()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(MakeCandidate());
            journal.RecordOutcome(MakeOutcome());
        }
        JournalExporter.ExportCsv(DbPath, _dir);

        string[] candidateLines = File.ReadAllLines(Path.Combine(_dir, "candidates.csv"));
        Assert.Equal(2, candidateLines.Length);
        Assert.StartsWith("id,ts_ns,session_date,setup,direction,", candidateLines[0]);
        Assert.EndsWith(",-625,14,-639", candidateLines[1]); // gross, commission, net

        string[] fillLines = File.ReadAllLines(Path.Combine(_dir, "fills.csv"));
        Assert.Equal(3, fillLines.Length); // header + entry + exit
    }

    [Fact]
    public async Task ParquetExport_RoundTripsRowCountAndValues()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(MakeCandidate(1));
            journal.RecordCandidate(MakeCandidate(2, RiskBlock.Spread));
            journal.RecordOutcome(MakeOutcome(1));
        }
        await JournalExporter.ExportParquetAsync(DbPath, _dir);

        string path = Path.Combine(_dir, "candidates.parquet");
        using var reader = await Parquet.ParquetReader.CreateAsync(path);
        using var rowGroup = reader.OpenRowGroupReader(0);
        var idField = reader.Schema.DataFields.Single(f => f.Name == "id");
        var ids = await rowGroup.ReadColumnAsync(idField);
        Assert.Equal(2, ids.Data.Length);
        var netField = reader.Schema.DataFields.Single(f => f.Name == "net_pnl");
        var nets = await rowGroup.ReadColumnAsync(netField);
        Assert.Equal(-639.0, ((double?[])nets.Data)[0]);
        Assert.Null(((double?[])nets.Data)[1]);
    }
}
