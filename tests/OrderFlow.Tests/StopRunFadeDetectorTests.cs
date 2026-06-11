using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Setup 2 (stop-run fade) state machine driven through a REAL FeatureEngine with a fake
/// execution port and journal.
///
/// Master short scenario (June 1 2026 UTC; RTH opens 13:30 UTC = 09:30 EDT):
///   13:00     an overnight buy at 5000.00 sets the overnight high (the reference H).
///   13:31     two 1000-lot bars (at 4999.50 then 4999.00) seed the climax bar distribution
///             (so B3 has a percentile to clear) and give the 60s ATR a range.
///   13:32:30  a 40-lot buy sweeps 1 tick above H to 5000.25 (climax) → ContextMet; a second
///             40-lot buy extends the sweep high to 5000.50.
///   13:32:35+ sells pull back, printing three stacked diagonal sell imbalances at 4999.75 /
///             4999.50 / 4999.25 (B5) with no follow-through (B4) → Armed → sell stop at H − 2 = 4999.50.
/// </summary>
public class StopRunFadeDetectorTests
{
    private static ulong Ts(int hh, int mm, int ss) =>
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, hh, mm, ss, TimeSpan.Zero)).UnixNanos;

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
        public readonly StopRunFadeDetector Detector;
        public decimal BidPx = 4999.00m;
        public decimal AskPx = 4999.25m;
        private readonly TradeDirection _dir;
        private long _seq;

        public Harness(
            TradeDirection direction = TradeDirection.Short,
            Setup2Options? opts = null,
            RiskOptions? riskOpts = null)
        {
            _dir = direction;
            var fe = new FeatureEngineOptions
            {
                SessionMinSamples = 1, AtrBarSeconds = 60, AtrPeriodBars = 1,
                BarVolumeSize = 1000, MinBarSamples = 2,
            };
            var atrStore = new InMemoryAtrHistoryStore();
            double[] days = { 0.4, 0.7, 1.0, 1.3, 1.6 }; // live ATR ≈ 1.0 lands mid-band
            for (int i = 0; i < days.Length; i++)
            {
                atrStore.AddSample(new DateOnly(2026, 5, 25 + i), days[i]);
            }
            Engine = new FeatureEngine(TickSize.Es, fe, atrHistoryStore: atrStore);
            var risk = riskOpts ?? new RiskOptions();
            Risk = new RiskManager(risk);
            Detector = new StopRunFadeDetector(
                TickSize.Es, opts ?? new Setup2Options(), direction, Risk, Exec, Journal,
                new ExecutionOptions(), risk, () => ++_seq);
        }

        // Geometry is authored short-space (fade a swept high); the long mirror reflects every
        // price around 5000.00 and flips aggressor sides + book sides.
        private decimal Mx(decimal p) => _dir == TradeDirection.Long ? 10000m - p : p;
        private Side Sx(Side s) => _dir == TradeDirection.Long
            ? s == Side.Bid ? Side.Ask : s == Side.Ask ? Side.Bid : Side.None
            : s;

        private BookLevels Book()
        {
            var top = ((decimal?)Mx(BidPx), 50u, 1u);
            var bottom = ((decimal?)Mx(AskPx), 50u, 1u);
            return _dir == TradeDirection.Long
                ? TestEvents.Levels(new[] { bottom }, new[] { top }) // mirrored bid/ask swap
                : TestEvents.Levels(new[] { top }, new[] { bottom });
        }

        private void Feed(in MarketEvent e)
        {
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
            Detector.OnEvent(in e, Tracker, Engine);
        }

        public void Trade(Side aggressor, decimal px, uint size, ulong ts) =>
            Feed(TestEvents.Trade(Sx(aggressor), Mx(px), size, Book(), ts));

        public void Fill(long orderId, decimal px, long qty, ulong ts)
        {
            var spec = Exec.Spec(orderId);
            Exec.Filled.Add(orderId);
            Detector.OnFill(new Fill(orderId, new Timestamp(ts), spec.Side, Price.FromDecimal(Mx(px)), qty), Engine);
        }

        /// <summary>Overnight high + two completed bars + ATR range.</summary>
        public void Prelude()
        {
            Trade(Side.Bid, 5000.00m, 10, Ts(13, 0, 0));    // overnight high 5000.00
            Trade(Side.Ask, 4999.50m, 1000, Ts(13, 31, 0)); // bar 1 (ATR high)
            Trade(Side.Ask, 4999.00m, 1000, Ts(13, 31, 40)); // bar 2 (ATR low)
        }

        /// <summary>Sweep to 5000.50 → ContextMet.</summary>
        public void Sweep()
        {
            Trade(Side.Bid, 5000.25m, 40, Ts(13, 32, 30)); // 1 tick above H → climax / context
            Trade(Side.Bid, 5000.50m, 40, Ts(13, 32, 31)); // extends the sweep high
        }

        /// <summary>Pullback printing three stacked sell imbalances → B5 → Armed.</summary>
        public void Arm()
        {
            Prelude();
            Sweep();
            BidPx = 4999.00m;
            AskPx = 4999.25m;
            Trade(Side.Ask, 5000.25m, 5, Ts(13, 32, 35));  // first pullback → freezes the sweep high
            Trade(Side.Ask, 4999.75m, 10, Ts(13, 32, 36)); // sell imbalance 1
            Trade(Side.Ask, 4999.50m, 10, Ts(13, 32, 37)); // sell imbalance 2
            Trade(Side.Ask, 4999.25m, 10, Ts(13, 32, 38)); // sell imbalance 3 → B5
        }
    }

    [Fact]
    public void Context_Met_OnClimaxSweepOfOvernightHigh()
    {
        var h = new Harness();
        h.Prelude();
        h.Sweep();
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
    }

    [Fact]
    public void Arm_PlacesSellStopInsideLevel_AndJournalsCandidate()
    {
        var h = new Harness();
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);

        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.StopMarket, spec.Type);
        Assert.Equal(Side.Ask, spec.Side);
        Assert.Equal(Price.FromDecimal(4999.50m), spec.Price); // H − 2 ticks
        Assert.Equal(6, spec.Quantity); // $500 / (6 ticks × $12.50)

        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.None, cand.Block);
        Assert.Equal(SetupId.StopRunFade, cand.Setup);
        Assert.Equal(Price.FromDecimal(5000.00m), cand.Level); // the swept reference high
        Assert.True(h.Risk.Exposed);
    }

    [Fact]
    public void Funnel_CountsTheConditionChain_ThroughArm()
    {
        var h = new Harness();
        h.Arm();
        var c = h.Detector.Conditions;
        Assert.True(c.Passed("B1B2") >= 1);
        Assert.True(c.Passed("B3") >= 1);
        Assert.True(c.Passed("B4") >= 1);
        Assert.Equal(1, c.Passed("B5"));
        // B5 was evaluated on every in-context event before the third imbalance stacked.
        Assert.True(c.Evaluated("B5") > c.Passed("B5"));
        Assert.Contains("B1B2", h.Detector.FunnelLine());
    }

    [Fact]
    public void Context_Resets_OnFollowThroughBeyondSweep()
    {
        var h = new Harness();
        h.Prelude();
        h.Sweep();
        h.Trade(Side.Ask, 5000.25m, 5, Ts(13, 32, 35));  // pullback freezes the sweep high (5000.50)
        h.Trade(Side.Bid, 5001.00m, 5, Ts(13, 32, 36));  // new high 2 ticks beyond → follow-through
        Assert.Equal(SetupState.Idle, h.Detector.State);
    }

    private static (Harness H, long StopId, long T1Id) InPosition()
    {
        var h = new Harness();
        h.Arm();
        long entryId = h.Exec.Live()[0].Id;
        h.Fill(entryId, 4999.25m, 6, Ts(13, 32, 40)); // stop trigger 4999.50 − 1 tick slippage
        Assert.Equal(SetupState.InPosition, h.Detector.State);
        var live = h.Exec.Live();
        Assert.Equal(2, live.Count);
        var stop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        var t1 = live.Single(o => o.Spec.Type == OrderType.Limit);
        return (h, stop.Id, t1.Id);
    }

    [Fact]
    public void EntryFill_PlacesStopAboveSweep_AndT1()
    {
        var (h, stopId, t1Id) = InPosition();
        Assert.Equal(Price.FromDecimal(5000.75m), h.Exec.Spec(stopId).Price); // sweep high 5000.50 + 1 tick
        Assert.Equal(Side.Bid, h.Exec.Spec(stopId).Side);
        Assert.Equal(6, h.Exec.Spec(stopId).Quantity);
        Assert.Equal(Price.FromDecimal(4997.75m), h.Exec.Spec(t1Id).Price);  // entry − 1R (R = 6 ticks)
        Assert.Equal(3, h.Exec.Spec(t1Id).Quantity);
    }

    [Fact]
    public void T1Fill_MovesStopToEntry_AndPlacesT2()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Fill(t1Id, 4997.75m, 3, Ts(13, 32, 50));
        Assert.Contains(stopId, h.Exec.Cancelled);
        var newStop = h.Exec.Live().Single(o => o.Spec.Type == OrderType.StopMarket);
        Assert.Equal(Price.FromDecimal(4999.25m), newStop.Spec.Price); // entry (breakeven offset 0)
        Assert.Equal(SetupState.InPosition, h.Detector.State);
    }

    [Fact]
    public void StopFill_Finalizes_Loss()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Fill(stopId, 5001.00m, 6, Ts(13, 32, 55)); // trigger 5000.75 + 1 tick slippage
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Contains(t1Id, h.Exec.Cancelled);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Stop, outcome.ExitReason);
        Assert.Equal(-525m, outcome.GrossPnl); // −7 ticks × $12.50 × 6
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void Scratch_ExitsAtMarket_WhenPriceHoldsBackPastTheLevel()
    {
        var (h, stopId, t1Id) = InPosition();
        h.Trade(Side.Bid, 5000.25m, 5, Ts(13, 33, 0));  // back above H → start the scratch clock
        h.Trade(Side.Bid, 5000.25m, 5, Ts(13, 34, 1));  // still above H 61s later → scratch
        Assert.Contains(stopId, h.Exec.Cancelled);
        Assert.Contains(t1Id, h.Exec.Cancelled);
        var (exitId, exitSpec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exitSpec.Type);
        h.Fill(exitId, 5000.25m, 6, Ts(13, 34, 2));
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Scratch, outcome.ExitReason);
        Assert.Equal(SetupState.Closed, h.Detector.State);
    }

    [Fact]
    public void Entry_Expires_FourMinutesAfterSweep()
    {
        var h = new Harness();
        h.Arm();
        h.Trade(Side.Ask, 4999.00m, 1, Ts(13, 36, 31)); // 241s after the 13:32:30 sweep
        Assert.Equal(SetupState.Closed, h.Detector.State);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Expired, outcome.Disposition);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void LongMirror_ArmsWithBuyStopAboveTheSweptLow()
    {
        var h = new Harness(direction: TradeDirection.Long);
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);
        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.StopMarket, spec.Type);
        Assert.Equal(Side.Bid, spec.Side);
        Assert.Equal(Price.FromDecimal(5000.50m), spec.Price); // mirror of H − 2 (4999.50 → 5000.50)
        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(Price.FromDecimal(5000.00m), cand.Level);
    }
}
