using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Setup 5 (delta divergence fade) state machine driven through a REAL FeatureEngine with a
/// fake execution port and journal. Geometry is authored in "short space" (fade a new high);
/// the long mirror reflects every price around 5000.00 and flips aggressor sides.
///
/// Master short scenario (seconds from 14:00:00 UTC = 10:00 EDT):
///   t0–2   buys lift to a 4999.50 swing high on strong cumDelta (+200).
///   t10–20 sells pull back without a new high, bleeding cumDelta to +70.
///   t65    a weak buy prints a new high H2 = 5000.00 (round-number LOI) on cumDelta +80 ≪ the
///          prior swing's +200 → E1 (2 ticks beyond 4999.50) + E2 (cumDelta divergence) + E3
///          (on the LOI). Also closes the first 60s ATR bar.
///   t70    a sell reclaims below the prior swing high (E4) → Armed → sell limit at H2 − 2 = 4999.50.
/// </summary>
public class DeltaDivergenceFadeDetectorTests
{
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private sealed class FakeExec : IExecutionPort
    {
        public readonly List<(long Id, OrderSpec Spec)> Placed = new();
        public readonly HashSet<long> Cancelled = new();
        public readonly HashSet<long> Filled = new();
        private long _next;

        public long Place(in OrderSpec spec)
        {
            Placed.Add((++_next, spec));
            return _next;
        }

        public bool Cancel(long orderId)
        {
            if (Cancelled.Contains(orderId) || Filled.Contains(orderId))
            {
                return false;
            }
            Cancelled.Add(orderId);
            return true;
        }

        public OrderSpec Spec(long id) => Placed.First(p => p.Id == id).Spec;

        public List<(long Id, OrderSpec Spec)> Live() =>
            Placed.Where(p => !Cancelled.Contains(p.Id) && !Filled.Contains(p.Id)).ToList();
    }

    private sealed class FakeJournal : ICandidateJournal
    {
        public readonly List<CandidateRecord> Candidates = new();
        public readonly List<CandidateOutcome> Outcomes = new();
        public void RecordCandidate(CandidateRecord record) => Candidates.Add(record);
        public void RecordOutcome(CandidateOutcome outcome) => Outcomes.Add(outcome);
    }

    private sealed class Harness
    {
        public readonly BookStateTracker Tracker = new();
        public readonly FeatureEngine Engine;
        public readonly RiskManager Risk;
        public readonly FakeExec Exec = new();
        public readonly FakeJournal Journal = new();
        public readonly DeltaDivergenceFadeDetector Detector;
        private readonly TradeDirection _dir;
        private long _seq;

        public Harness(
            TradeDirection direction = TradeDirection.Short,
            Setup5Options? opts = null,
            RiskOptions? riskOpts = null)
        {
            _dir = direction;
            var fe = new FeatureEngineOptions { SessionMinSamples = 1, AtrBarSeconds = 60, AtrPeriodBars = 1 };
            var atrStore = new InMemoryAtrHistoryStore();
            for (int i = 0; i < 5; i++)
            {
                atrStore.AddSample(new DateOnly(2026, 5, 25 + i), i * 2.0); // 0,2,4,6,8 — live lands mid-band
            }
            Engine = new FeatureEngine(TickSize.Es, fe, atrHistoryStore: atrStore);
            var risk = riskOpts ?? new RiskOptions();
            Risk = new RiskManager(risk);
            Detector = new DeltaDivergenceFadeDetector(
                TickSize.Es, opts ?? new Setup5Options(), direction, Risk, Exec, Journal,
                new ExecutionOptions(), risk, () => ++_seq);
        }

        private decimal Mx(decimal p) => _dir == TradeDirection.Long ? 10000m - p : p;
        private Side Sx(Side s) => _dir == TradeDirection.Long
            ? s == Side.Bid ? Side.Ask : s == Side.Ask ? Side.Bid : Side.None
            : s;

        private BookLevels Book()
        {
            // short: bid 4999.75 / ask 5000.00 (spread 1). long mirror reflects + swaps sides.
            var bid = ((decimal?)Mx(4999.75m), 50u, 1u);
            var ask = ((decimal?)Mx(5000.00m), 50u, 1u);
            return _dir == TradeDirection.Long
                ? TestEvents.Levels(new[] { ((decimal?)Mx(5000.00m), 50u, 1u) }, new[] { ((decimal?)Mx(4999.75m), 50u, 1u) })
                : TestEvents.Levels(new[] { bid }, new[] { ask });
        }

        private void Feed(in MarketEvent e)
        {
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
            Detector.OnEvent(in e, Tracker, Engine);
        }

        public void Trade(Side aggressor, decimal px, uint size, double t) =>
            Feed(TestEvents.Trade(Sx(aggressor), Mx(px), size, Book(), At(t)));

        public void BookEvt(double t) =>
            Feed(TestEvents.BookChanged(BookCause.Modify, Side.None, 0.25m, 0, 0, Book(), ts: At(t)));

        public void Fill(long orderId, decimal px, long qty, double t)
        {
            var spec = Exec.Spec(orderId);
            Exec.Filled.Add(orderId);
            Detector.OnFill(new Fill(orderId, new Timestamp(At(t)), spec.Side, Price.FromDecimal(Mx(px)), qty), Engine);
        }

        /// <summary>Runs the scenario up to the t65 new-high divergence (context).</summary>
        public void RunToContext()
        {
            Trade(Side.Bid, 4999.00m, 50, 0);
            Trade(Side.Bid, 4999.25m, 50, 1);
            Trade(Side.Bid, 4999.50m, 100, 2); // leg peak 4999.50 @ cumDelta +200
            Trade(Side.Ask, 4999.25m, 60, 10);
            Trade(Side.Ask, 4998.50m, 70, 20); // 4-tick pullback confirms 4999.50 as the swing high; cumDelta +70
            Trade(Side.Bid, 5000.00m, 10, 65); // new high H2 (2 ticks beyond) on cumDelta +80 ≪ +200; closes ATR bar
        }

        /// <summary>Runs through the t70 reclaim trigger → Armed.</summary>
        public void Arm()
        {
            RunToContext();
            Trade(Side.Ask, 4999.25m, 5, 70); // reclaim below the prior swing → E4
        }
    }

    [Fact]
    public void Context_Met_OnNewHighWithCumDeltaDivergenceAtLoi()
    {
        var h = new Harness();
        h.RunToContext();
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
    }

    [Fact]
    public void Arm_PlacesSellLimitInsideExtreme_AndJournalsCandidate()
    {
        var h = new Harness();
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);

        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Limit, spec.Type);
        Assert.Equal(Side.Ask, spec.Side);
        Assert.Equal(Price.FromDecimal(4999.50m), spec.Price); // H2 − 2 ticks
        Assert.Equal(10, spec.Quantity); // $500 / (4 ticks × $12.50)

        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.None, cand.Block);
        Assert.Equal(SetupId.DeltaDivergenceFade, cand.Setup);
        Assert.Equal(Price.FromDecimal(5000.00m), cand.Level); // the new high H2
        Assert.True(h.Risk.Exposed);
    }

    private static (Harness H, long StopId, long T1Id) InPosition()
    {
        var h = new Harness();
        h.Arm();
        long entryId = h.Exec.Live()[0].Id;
        h.Fill(entryId, 4999.50m, 10, 71);
        Assert.Equal(SetupState.InPosition, h.Detector.State);
        var live = h.Exec.Live();
        Assert.Equal(2, live.Count);
        var stop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        var t1 = live.Single(o => o.Spec.Type == OrderType.Limit);
        return (h, stop.Id, t1.Id);
    }

    [Fact]
    public void EntryFill_PlacesStopAboveExtreme_AndT1()
    {
        var (h, stopId, t1Id) = InPosition();
        Assert.Equal(Price.FromDecimal(5000.50m), h.Exec.Spec(stopId).Price); // H2 + 2 ticks
        Assert.Equal(10, h.Exec.Spec(stopId).Quantity);
        Assert.Equal(Price.FromDecimal(4998.50m), h.Exec.Spec(t1Id).Price);   // entry − 1R (R = 4 ticks)
        Assert.Equal(5, h.Exec.Spec(t1Id).Quantity);
    }

    [Fact]
    public void T1Fill_MovesStopToEntry_AndPlacesT2()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Fill(t1Id, 4998.50m, 5, 80);
        Assert.Contains(stopId, h.Exec.Cancelled);
        var live = h.Exec.Live();
        var newStop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        Assert.Equal(Price.FromDecimal(4999.50m), newStop.Spec.Price); // entry (breakeven offset 0)
        Assert.Equal(SetupState.InPosition, h.Detector.State);
    }

    [Fact]
    public void StopFill_Finalizes_Loss()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Fill(stopId, 5000.75m, 10, 80); // trigger 5000.50 + 1 tick slippage
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Contains(t1Id, h.Exec.Cancelled);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Stop, outcome.ExitReason);
        // (5000.75 − 4999.50) = 5 ticks against a short → −5 × $12.50 × 10 = −$625.
        Assert.Equal(-625m, outcome.GrossPnl);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void Invalidation_ExitsAtMarket_WhenRealBuyersReturnAbovePriorSwing()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Trade(Side.Bid, 5000.00m, 150, 73); // +150 bucket above the 4999.50 prior swing
        Assert.Contains(stopId, h.Exec.Cancelled);
        Assert.Contains(t1Id, h.Exec.Cancelled);
        var (exitId, exitSpec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exitSpec.Type);
        h.Fill(exitId, 5000.00m, 10, 73.5);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Invalidation, outcome.ExitReason);
    }

    [Fact]
    public void Entry_Expires_AfterTwoMinutes()
    {
        var h = new Harness();
        h.Arm();
        h.BookEvt(191); // 121s after the t70 arm
        Assert.Equal(SetupState.Closed, h.Detector.State);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Expired, outcome.Disposition);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void Funnel_CountsTheConditionChain()
    {
        var h = new Harness();
        h.Arm();
        var c = h.Detector.Conditions;
        // One fresh new-extreme sample (t65) passes E1–E3; E4 is evaluated at t65 (fails —
        // no imbalance, no reclaim yet) and at the t70 reclaim (passes).
        Assert.Equal(1, c.Evaluated("E1"));
        Assert.Equal(1, c.Passed("E1"));
        Assert.Equal(1, c.Passed("E2"));
        Assert.Equal(1, c.Passed("E3"));
        Assert.Equal(2, c.Evaluated("E4"));
        Assert.Equal(1, c.Passed("E4"));
        Assert.Contains("E1 1/1", h.Detector.FunnelLine());
    }

    [Fact]
    public void Funnel_AttributesTheBindingConstraint_WhenLocationFails()
    {
        // Same scenario, but the LOI proximity is too tight for the 5000.00 round number
        // to count — E3 becomes the binding constraint and no context forms.
        var h = new Harness(opts: new Setup5Options { LocationProximityTicks = -1 });
        h.Arm();
        Assert.Equal(SetupState.Idle, h.Detector.State);
        var c = h.Detector.Conditions;
        Assert.Equal(1, c.Passed("E1"));
        Assert.Equal(1, c.Passed("E2"));
        Assert.Equal(1, c.Evaluated("E3"));
        Assert.Equal(0, c.Passed("E3"));
        Assert.Equal(0, c.Evaluated("E4"));
    }

    [Fact]
    public void LongMirror_ArmsWithBuyLimitInsideTheLow()
    {
        var h = new Harness(direction: TradeDirection.Long);
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);
        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Limit, spec.Type);
        Assert.Equal(Side.Bid, spec.Side);
        Assert.Equal(Price.FromDecimal(5000.50m), spec.Price); // mirror of H2 − 2 (5000.00 → 5000.50)
        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(Price.FromDecimal(5000.00m), cand.Level); // mirror of 5000.00
    }
}
