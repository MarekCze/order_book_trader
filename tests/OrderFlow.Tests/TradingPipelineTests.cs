using OrderFlow.Application.Pipeline;
using OrderFlow.Backtest;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// End-to-end M4 wiring: events → feature stage → trading stage (fill simulator resolves
/// fills BEFORE detectors react to the same event) → detector → orders → journal.
/// Uses the master absorption scenario from <see cref="AbsorptionFadeDetectorTests"/>,
/// then lets the REAL fill simulator execute the trade: the resting buy limit at L+1
/// fills on the next trade through it, the stop triggers on a print at/below L−3 and
/// fills with 1 tick adverse slippage.
/// </summary>
public class TradingPipelineTests
{
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private sealed class FakeJournal : ICandidateJournal
    {
        public readonly List<CandidateRecord> Candidates = new();
        public readonly List<CandidateOutcome> Outcomes = new();
        public void RecordCandidate(CandidateRecord record) => Candidates.Add(record);
        public void RecordOutcome(CandidateOutcome outcome) => Outcomes.Add(outcome);
    }

    private sealed class Pipeline
    {
        public readonly FakeJournal Journal = new();
        public readonly BookStateTrackerStage Trackers = new();
        public readonly FeatureEngineStage Features;
        public readonly TradingStage Trading;
        private readonly CompositeBookEventObserver _observer;
        public uint BidSize = 100;
        public uint AskSize = 50;

        public Pipeline()
        {
            var feOpts = new FeatureEngineOptions
            {
                SessionMinSamples = 1,
                AtrBarSeconds = 60,
                AtrPeriodBars = 1,
            };
            var atrStore = new InMemoryAtrHistoryStore();
            for (int i = 0; i < 5; i++)
            {
                atrStore.AddSample(new DateOnly(2026, 5, 25 + i), i < 3 ? 0.0 : 5.0);
            }
            Features = new FeatureEngineStage(TickSize.Es, feOpts, atrHistoryStore: atrStore);
            var execOpts = new ExecutionOptions();
            var riskOpts = new RiskOptions();
            var tradingOpts = new TradingOptions { Setup1 = new Setup1Options { DeltaPercentile = -1.0 } };
            Trading = new TradingStage(Features, (engine, nextCandidateId) =>
            {
                var simulator = new FillSimulator(TickSize.Es, execOpts);
                var coordinator = new TradingCoordinator(
                    TickSize.Es, tradingOpts, riskOpts, execOpts, simulator, Journal, engine, nextCandidateId);
                simulator.Listener = coordinator;
                return (coordinator, simulator);
            });
            _observer = new CompositeBookEventObserver(Features, Trading);
        }

        public void Feed(in MarketEvent e)
        {
            var tracker = Trackers.GetOrCreateTracker(e.InstrumentId);
            tracker.Apply(in e);
            _observer.OnEventApplied(in e, tracker);
        }

        private BookLevels Book() => TestEvents.Levels(
            new (decimal?, uint, uint)[] { (5000.00m, BidSize, 5) },
            new (decimal?, uint, uint)[] { (5000.25m, AskSize, 5) });

        public void Trade(Side aggressor, decimal px, uint size, double t) =>
            Feed(TestEvents.Trade(aggressor, px, size, Book(), At(t)));

        public void Bid(uint size, double t)
        {
            BidSize = size;
            Feed(TestEvents.BookChanged(BookCause.Modify, Side.Bid, 5000.00m, size, 0, Book(), ts: At(t)));
        }

        public void RunMasterScenarioToArm()
        {
            for (int i = 0; i < 6; i++)
            {
                Trade(Side.Bid, 5001.00m + i * 0.25m, 10, i * 5);
            }
            Trade(Side.Bid, 5001.00m, 5, 61);
            decimal[] decline = { 5001.50m, 5001.25m, 5001.00m, 5000.75m, 5000.50m, 5000.25m };
            for (int i = 0; i < decline.Length; i++)
            {
                Trade(Side.Ask, decline[i], 60, 65 + i * 5);
            }
            Trade(Side.Ask, 5000.00m, 80, 91);
            Bid(40, 92);
            Bid(100, 93);
            Trade(Side.Ask, 5000.00m, 70, 95);
            Trade(Side.Ask, 5000.00m, 50, 101);
            Bid(35, 103);
            Bid(110, 104);
            Trade(Side.Ask, 5000.00m, 30, 105);
            Trade(Side.Ask, 5000.00m, 60, 111);
            Bid(30, 113);
            Bid(120, 114);
            Trade(Side.Ask, 5000.00m, 25, 121);
            Trade(Side.Ask, 5000.00m, 10, 131);
            Trade(Side.Bid, 5000.25m, 12, 135);
            Bid(120, 141); // trigger event → Armed → buy limit 5000.25 × 10 resting
        }
    }

    [Fact]
    public void EndToEnd_EntryFillsOnTradeThrough_StopsOut_AndJournalsOutcome()
    {
        var p = new Pipeline();
        p.RunMasterScenarioToArm();

        var candidate = Assert.Single(p.Journal.Candidates);
        Assert.Equal(RiskBlock.None, candidate.Block);
        Assert.Equal(SetupId.AbsorptionFade, candidate.Setup);
        Assert.Empty(p.Journal.Outcomes); // order working, nothing resolved yet

        // A trade AT L = strictly through the L+1 buy limit → entry fills at 5000.25.
        p.Trade(Side.Ask, 5000.00m, 10, 142);
        Assert.Empty(p.Journal.Outcomes); // in position now

        // A print at L−4 triggers the L−3 stop; conservative fill at trigger − 1 tick.
        p.Trade(Side.Ask, 4999.25m, 5, 150);

        var outcome = Assert.Single(p.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Traded, outcome.Disposition);
        Assert.Equal(ExitReason.Stop, outcome.ExitReason);
        Assert.NotNull(outcome.EntryFill);
        Assert.Equal(Price.FromDecimal(5000.25m), outcome.EntryFill!.Value.Price);
        Assert.Equal(10, outcome.EntryFill.Value.Quantity);
        var exit = Assert.Single(outcome.ExitFills);
        Assert.Equal(Price.FromDecimal(4999.00m), exit.Price); // 4999.25 − 1 tick slippage
        Assert.Equal(10, exit.Quantity);
        // (4999.00 − 5000.25) = −5 ticks × $12.50 × 10 − $14 commission.
        Assert.Equal(-639m, outcome.NetPnl);
        Assert.False(outcome.T1Filled);
        Assert.Equal(1, outcome.MaeTicks); // the 5000.00 fill-event print, 1 tick under entry
    }

    [Fact]
    public void EndToEnd_TargetPath_T1ThenT2()
    {
        var p = new Pipeline();
        p.RunMasterScenarioToArm();
        p.Trade(Side.Ask, 5000.00m, 10, 142);  // entry fill 5000.25 × 10
        p.Trade(Side.Bid, 5001.50m, 5, 160);   // through T1 (5001.25) → exits 5 at 5001.25
        Assert.Empty(p.Journal.Outcomes);
        p.Trade(Side.Bid, 5003.50m, 5, 200);   // through T2 cap (5003.25) → exits remaining 5

        var outcome = Assert.Single(p.Journal.Outcomes);
        Assert.Equal(ExitReason.Target, outcome.ExitReason);
        Assert.True(outcome.T1Filled);
        Assert.Equal(2, outcome.ExitFills.Count);
        // 5 × (+4 ticks) + 5 × (+12 ticks) = 80 ticks × $12.50 = $1000 − $14.
        Assert.Equal(986m, outcome.NetPnl);
    }
}
