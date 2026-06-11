using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Setup 4 (LVN vacuum) state machine driven through a REAL FeatureEngine with a fake
/// execution port and journal, so D1–D3 read the same profile / liquidity / flow state
/// production reads.
///
/// Master short scenario (seconds from 14:00:00 UTC = 10:00 EDT):
///   profile  one trade per tick across 4997.25–5000.00 builds a volume profile with an
///            LVN at 4999.50 (1 lot) and an HVN at 4997.50 (80 lots, 8 ticks below).
///   t0–61    those trades also seed the delta distribution and close an ATR bar.
///   book     a 4-level bid stack rests the whole time: 5000.00 / 4999.75 / 4999.50 / 4999.25.
///   t100     beyond-best bid depth (4999.75+4999.50+4999.25) = 300 is the lagged baseline.
///   t105     a +20 add at 4999.25 gives the pull ratio a denominator.
///   t110–126 three 40-lot sell prints at 5000.00 → delta(30s) = −120 (D3) and lift the
///            5000.00 profile bin (keeping the LVN/HVN intact).
///   t130     beyond-best collapses to 180 (−40% vs the lagged 300, cancels 140 ≫ adds 20):
///            D2 holds, D1 still holds (last price 5000.00 is 2 ticks above the 4999.50 LVN,
///            HVN 8 ticks below) → Armed → sell stop at LVN − 1 = 4999.25.
/// </summary>
public class LvnVacuumDetectorTests
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
        public readonly LvnVacuumDetector Detector;
        private readonly TradeDirection _dir;
        // Geometry is authored in "short space" (downside vacuum); the long mirror reflects
        // every price around 5000.00 and flips aggressor sides, so one scenario covers both.
        private readonly (decimal Px, uint Sz)[] _stack; // pull-side levels (bids short / asks long)
        private readonly (decimal Px, uint Sz)[] _other; // opposite touch (asks short / bids long)
        private long _seq;

        public Harness(
            TradeDirection direction = TradeDirection.Short,
            LvnVacuumOptions? opts = null,
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
            Detector = new LvnVacuumDetector(
                TickSize.Es, opts ?? new LvnVacuumOptions(), direction, Risk, Exec, Journal,
                new ExecutionOptions(), risk, () => ++_seq);
            _stack = new[] { (5000.00m, 50u), (4999.75m, 100u), (4999.50m, 100u), (4999.25m, 100u) };
            _other = new[] { (5000.25m, 50u), (5000.50m, 50u) };
        }

        // ----- short→long mirror -----
        private decimal Mx(decimal p) => _dir == TradeDirection.Long ? 10000m - p : p;
        private Side Sx(Side s) => _dir == TradeDirection.Long
            ? s == Side.Bid ? Side.Ask : s == Side.Ask ? Side.Bid : Side.None
            : s;

        private BookLevels Book()
        {
            var stack = _stack.Select(b => ((decimal?)Mx(b.Px), b.Sz, 1u)).ToArray();
            var other = _other.Select(a => ((decimal?)Mx(a.Px), a.Sz, 1u)).ToArray();
            // short: stack = bids, other = asks. long mirror: stack = asks, other = bids.
            return _dir == TradeDirection.Long
                ? TestEvents.Levels(other, stack)
                : TestEvents.Levels(stack, other);
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

        public void SetBidSizes(uint l1, uint l2, uint l3, double t)
        {
            _stack[1] = (_stack[1].Px, l1);
            _stack[2] = (_stack[2].Px, l2);
            _stack[3] = (_stack[3].Px, l3);
            BookEvt(t);
        }

        public void WidenAsk() => _other[0] = (5000.50m, _other[0].Sz); // widens the spread by 1 tick

        public void Fill(long orderId, decimal px, long qty, double t)
        {
            var spec = Exec.Spec(orderId);
            Exec.Filled.Add(orderId);
            Detector.OnFill(new Fill(orderId, new Timestamp(At(t)), spec.Side, Price.FromDecimal(px), qty), Engine);
        }

        public void BuildProfile()
        {
            // One trade per tick (buys, so they leave the 30s delta window well before signal);
            // book held constant. HVN at 4997.50, LVN at 4999.50.
            (decimal Px, uint Sz)[] bins =
            {
                (4997.25m, 10), (4997.50m, 80), (4997.75m, 10), (4998.00m, 10), (4998.25m, 10),
                (4998.50m, 10), (4998.75m, 10), (4999.00m, 10), (4999.25m, 10), (4999.50m, 1),
                (4999.75m, 10), (5000.00m, 10),
            };
            double t = 0;
            foreach (var (px, sz) in bins)
            {
                Trade(Side.Bid, px, sz, t);
                t += 2;
            }
            Trade(Side.Bid, 5000.00m, 1, 61); // closes the first 60s ATR bar
        }

        /// <summary>Runs the master scenario up to and including the t130 signal event.</summary>
        public void Arm()
        {
            BuildProfile();
            BookEvt(100);                       // lagged depth baseline = 300
            SetBidSizes(100, 100, 120, 105);    // +20 add at 4999.25 → pull-ratio denominator
            Trade(Side.Ask, 5000.00m, 40, 110); // D3 sells (book held → no bid churn)
            Trade(Side.Ask, 5000.00m, 40, 118);
            Trade(Side.Ask, 5000.00m, 40, 126);
            SetBidSizes(40, 40, 100, 130);      // beyond-best 320 → 180 (−40%), cancels 140
        }
    }

    [Fact]
    public void Context_Met_WhenPriceSitsOnLvnWithRoomToHvn()
    {
        var h = new Harness();
        h.BuildProfile();
        h.BookEvt(100);
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
    }

    [Fact]
    public void Funnel_CountsTheConditionChain_ThroughArm()
    {
        var h = new Harness();
        h.Arm();
        var c = h.Detector.Conditions;
        Assert.True(c.Passed("D1lvn") >= 1);
        Assert.True(c.Passed("D1room") >= 1);
        Assert.Equal(1, c.Passed("D2"));
        Assert.Equal(1, c.Passed("D3"));
        // The profile has no LVN until enough volume builds — D1lvn must show failures.
        Assert.True(c.Evaluated("D1lvn") > c.Passed("D1lvn"));
        Assert.Contains("D1lvn", h.Detector.FunnelLine());
    }

    [Fact]
    public void Arm_PlacesEntryStopBelowLvn_AndJournalsCandidate()
    {
        var h = new Harness();
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);

        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.StopMarket, spec.Type);
        Assert.Equal(Side.Ask, spec.Side);
        Assert.Equal(Price.FromDecimal(4999.25m), spec.Price); // LVN − 1 tick
        Assert.Equal(6, spec.Quantity); // $500 budget / (6 ticks × $12.50)

        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.None, cand.Block);
        Assert.Equal(SetupId.LvnVacuum, cand.Setup);
        Assert.Equal(Price.FromDecimal(4999.50m), cand.Level); // the LVN
        Assert.True(h.Risk.Exposed);
    }

    [Fact]
    public void Candidate_Blocked_OnWideSpread_IsJournaledAndResets()
    {
        var h = new Harness();
        h.BuildProfile();
        h.BookEvt(100);
        h.SetBidSizes(100, 100, 120, 105);
        h.Trade(Side.Ask, 5000.00m, 40, 110);
        h.Trade(Side.Ask, 5000.00m, 40, 118);
        h.Trade(Side.Ask, 5000.00m, 40, 126);
        h.WidenAsk();                  // spread 2 ticks at signal time → global filter 4
        h.SetBidSizes(40, 40, 100, 130);
        Assert.Equal(SetupState.Idle, h.Detector.State);
        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.Spread, cand.Block);
        Assert.Empty(h.Exec.Placed);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void Context_Resets_WhenPriceLeavesLvnProximity()
    {
        var h = new Harness();
        h.BuildProfile();
        h.BookEvt(100);
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
        // Price moves into the dense 10-lot zone, > 3 ticks from the 4999.50 LVN with no
        // other LVN within proximity → context drops.
        h.Trade(Side.Ask, 4998.50m, 5, 105);
        Assert.Equal(SetupState.Idle, h.Detector.State);
    }

    private static (Harness H, long EntryId) Armed()
    {
        var h = new Harness();
        h.Arm();
        return (h, h.Exec.Live()[0].Id);
    }

    private static (Harness H, long StopId, long TargetId) InPosition()
    {
        var (h, entryId) = Armed();
        h.Fill(entryId, 4999.00m, 6, 131); // stop trigger 4999.25 − 1 tick slippage
        Assert.Equal(SetupState.InPosition, h.Detector.State);
        var live = h.Exec.Live();
        Assert.Equal(2, live.Count);
        var stop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        var target = live.Single(o => o.Spec.Type == OrderType.Limit);
        return (h, stop.Id, target.Id);
    }

    [Fact]
    public void EntryFill_PlacesStopBeyondLvnZone_AndSingleTargetFrontRunningHvn()
    {
        var (h, stopId, targetId) = InPosition();
        Assert.Equal(Price.FromDecimal(5000.50m), h.Exec.Spec(stopId).Price);   // LVN + 4 ticks
        Assert.Equal(Side.Bid, h.Exec.Spec(stopId).Side);
        Assert.Equal(6, h.Exec.Spec(stopId).Quantity);
        Assert.Equal(Price.FromDecimal(4997.75m), h.Exec.Spec(targetId).Price); // HVN + 1 tick
        Assert.Equal(6, h.Exec.Spec(targetId).Quantity);                        // full size — no runners
    }

    [Fact]
    public void TargetFill_Finalizes_Win()
    {
        var (h, stopId, targetId) = InPosition();
        h.Fill(targetId, 4997.75m, 6, 140);
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Contains(stopId, h.Exec.Cancelled);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Target, outcome.ExitReason);
        // (4997.75 − 4999.00) = −5 ticks short = +5 ticks profit × $12.50 × 6 = $375 gross.
        Assert.Equal(375m, outcome.GrossPnl);
        Assert.Equal(8.40m, outcome.Commission);
        Assert.Equal(366.60m, outcome.NetPnl);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void StopFill_Finalizes_Loss()
    {
        var (h, stopId, targetId) = InPosition();
        h.Fill(stopId, 5000.75m, 6, 140); // trigger 5000.50 + 1 tick slippage
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Contains(targetId, h.Exec.Cancelled);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Stop, outcome.ExitReason);
        Assert.Equal(-525m, outcome.GrossPnl); // −7 ticks × $12.50 × 6
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void TimeStop_ExitsAtMarket_AfterThreeMinutes()
    {
        var (h, stopId, targetId) = InPosition();
        h.BookEvt(312); // 181s after the 131s entry
        Assert.Contains(stopId, h.Exec.Cancelled);
        Assert.Contains(targetId, h.Exec.Cancelled);
        var (exitId, exitSpec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exitSpec.Type);
        h.Fill(exitId, 4998.50m, 6, 312.5);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.TimeStop, outcome.ExitReason);
        Assert.Equal(SetupState.Closed, h.Detector.State);
    }

    [Fact]
    public void Entry_Expires_WhenUnfilled()
    {
        var (h, _) = Armed();
        h.BookEvt(191); // 61s after the t130 arm
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Empty(h.Exec.Live());
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Expired, outcome.Disposition);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void LongMirror_ArmsWithBuyStopAboveLvn()
    {
        // The whole scenario reflected around 5000.00: LVN 5000.50, HVN 5002.50, asks pulling,
        // aggressive buying → buy stop 1 tick above the LVN.
        var h = new Harness(direction: TradeDirection.Long);
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);
        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.StopMarket, spec.Type);
        Assert.Equal(Side.Bid, spec.Side);
        Assert.Equal(Price.FromDecimal(5000.75m), spec.Price); // LVN (5000.50) + 1 tick
        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(Price.FromDecimal(5000.50m), cand.Level);
    }
}
