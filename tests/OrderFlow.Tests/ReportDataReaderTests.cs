using OrderFlow.Backtest.Reporting;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;
using OrderFlow.Infrastructure.Journal;

namespace OrderFlow.Tests;

/// <summary>
/// The reporting reader pulls candidate rows back out of the journal SQLite database into
/// the pure <see cref="ReportRow"/> shape. It must read traded, blocked, and expired rows
/// alike and return them in journal (Id) order so the equity curve stays chronological.
/// </summary>
public class ReportDataReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ofreport").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DbPath => Path.Combine(_dir, "journal.db");

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
        return engine.ComputeSnapshot(tracker);
    }

    private static CandidateRecord Candidate(long id, SetupId setup, TradeDirection dir, RiskBlock block) => new()
    {
        Id = id,
        Ts = new Timestamp(1_780_000_000_000_000_000 + (ulong)id),
        SessionDate = new DateOnly(2026, 6, 1),
        Setup = setup,
        Direction = dir,
        Level = Price.FromDecimal(5000.00m),
        Block = block,
        Quantity = block == RiskBlock.None ? 10 : 0,
        Features = MakeSnapshot(),
    };

    [Fact]
    public void Reads_Traded_Blocked_Expired_InIdOrder()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(Candidate(1, SetupId.AbsorptionFade, TradeDirection.Long, RiskBlock.None));
            journal.RecordOutcome(new CandidateOutcome
            {
                CandidateId = 1,
                Disposition = CandidateDisposition.Traded,
                ExitReason = ExitReason.Target,
                EntryFill = new Fill(1, new Timestamp(1_780_000_000_500_000_000), Side.Bid, Price.FromDecimal(5000.00m), 10),
                ExitFills = new[] { new Fill(2, new Timestamp(1_780_000_001_000_000_000), Side.Ask, Price.FromDecimal(5002.00m), 10) },
                T1Filled = true,
                MaeTicks = 3,
                MfeTicks = 9,
                GrossPnl = 100m,
                Commission = 14m,
                NetPnl = 86m,
            });

            journal.RecordCandidate(Candidate(2, SetupId.StopRunFade, TradeDirection.Short, RiskBlock.Volatility));

            journal.RecordCandidate(Candidate(3, SetupId.LvnVacuum, TradeDirection.Long, RiskBlock.None));
            journal.RecordOutcome(new CandidateOutcome
            {
                CandidateId = 3,
                Disposition = CandidateDisposition.Expired,
                ExitReason = ExitReason.None,
                EntryFill = null,
                ExitFills = Array.Empty<Fill>(),
                T1Filled = false,
                MaeTicks = 0,
                MfeTicks = 0,
                GrossPnl = 0m,
                Commission = 0m,
                NetPnl = 0m,
            });
        }

        var rows = ReportDataReader.Read(DbPath);

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(r => r.Id).ToArray());

        var traded = rows[0];
        Assert.Equal(SetupId.AbsorptionFade, traded.Setup);
        Assert.Equal(TradeDirection.Long, traded.Direction);
        Assert.Equal("None", traded.Block);
        Assert.Equal(CandidateDisposition.Traded, traded.Disposition);
        Assert.Equal(ExitReason.Target, traded.ExitReason);
        Assert.True(traded.T1Filled);
        Assert.Equal(3, traded.MaeTicks);
        Assert.Equal(9, traded.MfeTicks);
        Assert.Equal(86m, traded.NetPnl);

        var blocked = rows[1];
        Assert.Equal(CandidateDisposition.Blocked, blocked.Disposition);
        Assert.Equal("Volatility", blocked.Block);
        Assert.Null(blocked.NetPnl);
        Assert.Equal(ExitReason.None, blocked.ExitReason);

        var expired = rows[2];
        Assert.Equal(CandidateDisposition.Expired, expired.Disposition);
        Assert.False(expired.Traded);
    }

    [Fact]
    public void EndToEnd_ReadThenCompute_ProducesExpectancy()
    {
        using (var journal = new SqliteCandidateJournal(DbPath, new FeatureEngineOptions()))
        {
            journal.RecordCandidate(Candidate(1, SetupId.AbsorptionFade, TradeDirection.Long, RiskBlock.None));
            journal.RecordOutcome(new CandidateOutcome
            {
                CandidateId = 1,
                Disposition = CandidateDisposition.Traded,
                ExitReason = ExitReason.Target,
                EntryFill = new Fill(1, new Timestamp(1_780_000_000_500_000_000), Side.Bid, Price.FromDecimal(5000.00m), 10),
                ExitFills = new[] { new Fill(2, new Timestamp(1_780_000_001_000_000_000), Side.Ask, Price.FromDecimal(5002.00m), 10) },
                T1Filled = true,
                MaeTicks = 3,
                MfeTicks = 9,
                GrossPnl = 100m,
                Commission = 14m,
                NetPnl = 86m,
            });
        }

        var report = BacktestReport.Compute(ReportDataReader.Read(DbPath));

        Assert.Equal(1, report.Overall.Trades);
        Assert.Equal(86m, report.Overall.Expectancy);
    }
}
