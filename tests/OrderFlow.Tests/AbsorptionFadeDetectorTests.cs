using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Setup 1 state machine: Idle → ContextMet → Armed → OrderWorking → InPosition → Closed,
/// driven by hand-crafted event sequences through a REAL FeatureEngine (so A2/A4/A5 read
/// the same state production reads) with a fake execution port and journal.
///
/// Master long scenario (seconds from 14:00:00 UTC = 10:00 EDT, L = 5000.00 round-number LOI):
///   t0–25   prelude: small buys at 5001.00–5002.25 (seeds delta distribution + baseline)
///   t61     one buy (closes the first 60s ATR bar → ATR + percentile available)
///   t65–90  decline: 6 sell prints of 60 from 5001.50 down to 5000.25
///           → at t90: decline 8 ticks, LOI dist 1, delta(60s) = −355 → ContextMet, stall starts
///   t91–135 stall at L: sells hitting the 5000.00 bid in decaying 10s buckets
///           (150/80/60/25/10), bid size dipping and refreshing 3× (max 120),
///           one buy print in the last bucket (delta +2 ≥ 0)
///   t141    trigger event: A3 (51s ≥ 45), A4 (325 ≥ 3×mean), A5 (325/120 ≥ 2.5, 3 refreshes),
///           A6 (10 ≤ 30% of 150, last delta +2) → Armed → buy limit 5000.25 × 10.
/// </summary>
public class AbsorptionFadeDetectorTests
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
        public readonly AbsorptionFadeDetector Detector;
        public uint BidSize = 100;
        public uint AskSize = 50;
        public decimal BidPx = 5000.00m;
        public decimal AskPx = 5000.25m;
        private long _seq;

        public Harness(
            TradeDirection direction = TradeDirection.Long,
            Setup1Options? opts = null,
            FeatureEngineOptions? feOpts = null,
            RiskOptions? riskOpts = null)
        {
            var fe = feOpts ?? new FeatureEngineOptions
            {
                SessionMinSamples = 1,
                AtrBarSeconds = 60,
                AtrPeriodBars = 1,
            };
            var atrStore = new InMemoryAtrHistoryStore();
            // 5 distinct prior days → filter 3's distribution exists; values straddle any
            // tiny live ATR so the percentile lands inside [0.20, 0.95].
            for (int i = 0; i < 5; i++)
            {
                atrStore.AddSample(new DateOnly(2026, 5, 25 + i), i < 3 ? 0.0 : 5.0);
            }
            Engine = new FeatureEngine(TickSize.Es, fe, atrHistoryStore: atrStore);
            var risk = riskOpts ?? new RiskOptions();
            Risk = new RiskManager(risk);
            // DeltaPercentile = −1 disables the percentile alternative so ContextMet fires
            // deterministically on the absolute threshold (the percentile path has its own test).
            var o = opts ?? new Setup1Options { DeltaPercentile = -1.0 };
            Detector = new AbsorptionFadeDetector(
                TickSize.Es, o, direction, Risk, Exec, Journal,
                new ExecutionOptions(), risk, () => ++_seq);
        }

        public BookLevels Book() => TestEvents.Levels(
            new (decimal?, uint, uint)[] { (BidPx, BidSize, 5) },
            new (decimal?, uint, uint)[] { (AskPx, AskSize, 5) });

        public void Feed(in MarketEvent e)
        {
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
            Detector.OnEvent(in e, Tracker, Engine);
        }

        public void Trade(Side aggressor, decimal px, uint size, double t) =>
            Feed(TestEvents.Trade(aggressor, px, size, Book(), At(t)));

        public void BookEvent(double t) =>
            Feed(TestEvents.BookChanged(BookCause.Modify, Side.Bid, BidPx, BidSize, 0, Book(), ts: At(t)));

        /// <summary>Book event changing the displayed bid size at the level.</summary>
        public void Bid(uint size, double t)
        {
            BidSize = size;
            BookEvent(t);
        }

        public void Fill(long orderId, decimal px, long qty, double t)
        {
            var spec = Exec.Spec(orderId);
            Exec.Filled.Add(orderId);
            Detector.OnFill(new Fill(orderId, new Timestamp(At(t)), spec.Side, Price.FromDecimal(px), qty), Engine);
        }

        public void RunPrelude()
        {
            for (int i = 0; i < 6; i++)
            {
                Trade(Side.Bid, 5001.00m + i * 0.25m, 10, i * 5);
            }
            Trade(Side.Bid, 5001.00m, 5, 61); // closes ATR bar 0
        }

        public void RunDecline(uint sellSize = 60)
        {
            decimal[] prices = { 5001.50m, 5001.25m, 5001.00m, 5000.75m, 5000.50m, 5000.25m };
            for (int i = 0; i < prices.Length; i++)
            {
                Trade(Side.Ask, prices[i], sellSize, 65 + i * 5);
            }
        }

        public void RunStall()
        {
            Trade(Side.Ask, 5000.00m, 80, 91);
            Bid(40, 92);
            Bid(100, 93);  // refresh 1
            Trade(Side.Ask, 5000.00m, 70, 95);
            Trade(Side.Ask, 5000.00m, 50, 101);
            Bid(35, 103);
            Bid(110, 104); // refresh 2
            Trade(Side.Ask, 5000.00m, 30, 105);
            Trade(Side.Ask, 5000.00m, 60, 111);
            Bid(30, 113);
            Bid(120, 114); // refresh 3
            Trade(Side.Ask, 5000.00m, 25, 121);
            Trade(Side.Ask, 5000.00m, 10, 131);
            Trade(Side.Bid, 5000.25m, 12, 135);
        }

        /// <summary>Runs the master scenario through the t141 trigger event.</summary>
        public void Arm()
        {
            RunPrelude();
            RunDecline();
            RunStall();
            BookEvent(141);
        }
    }

    // ----- context -----

    [Fact]
    public void Context_Met_OnDeclineIntoLoi_WithAggressiveSelling()
    {
        var h = new Harness();
        h.RunPrelude();
        Assert.Equal(SetupState.Idle, h.Detector.State);
        h.RunDecline();
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
    }

    [Fact]
    public void Context_NotMet_WhileDeltaBaselineUngated()
    {
        // CLAUDE.md: no detector may arm before the session percentile baseline is ready.
        var h = new Harness(feOpts: new FeatureEngineOptions
        {
            SessionMinSamples = 200,
            AtrBarSeconds = 60,
            AtrPeriodBars = 1,
        });
        h.RunPrelude();
        h.RunDecline();
        Assert.Equal(SetupState.Idle, h.Detector.State);
    }

    [Fact]
    public void Context_MetViaPercentileTail_WhenAbsoluteThresholdUnreached()
    {
        var h = new Harness(opts: new Setup1Options
        {
            DeltaContracts = 100_000, // absolute path unreachable
            DeltaPercentile = 0.10,
        });
        h.RunPrelude();
        h.RunDecline(sellSize: 20); // delta(60s) ≈ −115, but deeper than every sampled delta
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
    }

    [Fact]
    public void Context_Resets_WhenBidDropsBelowLevel()
    {
        var h = new Harness();
        h.RunPrelude();
        h.RunDecline();
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
        h.BidPx = 4999.75m; // defended best gives way
        h.BookEvent(95);
        Assert.Equal(SetupState.Idle, h.Detector.State);
    }

    [Fact]
    public void Context_Resets_WhenPriceRunsAwayFromLevel()
    {
        var h = new Harness();
        h.RunPrelude();
        h.RunDecline();
        h.Trade(Side.Bid, 5001.25m, 5, 95); // 5 ticks above L > StallAbandonTicks
        Assert.Equal(SetupState.Idle, h.Detector.State);
    }

    // ----- arming -----

    [Fact]
    public void Arm_PlacesEntryLimit_AndJournalsCandidate()
    {
        var h = new Harness();
        h.Arm();
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);

        var (id, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Limit, spec.Type);
        Assert.Equal(Side.Bid, spec.Side);
        Assert.Equal(Price.FromDecimal(5000.25m), spec.Price); // L + 1 tick
        Assert.Equal(10, spec.Quantity); // $500 budget / (4 ticks × $12.50)

        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.None, cand.Block);
        Assert.Equal(SetupId.AbsorptionFade, cand.Setup);
        Assert.Equal(TradeDirection.Long, cand.Direction);
        Assert.Equal(Price.FromDecimal(5000.00m), cand.Level);
        Assert.Equal(10, cand.Quantity);
        Assert.NotNull(cand.Features);
        Assert.Equal(1, cand.Features.SpreadTicks);
        Assert.True(h.Risk.Exposed);
    }

    [Fact]
    public void Candidate_Blocked_IsJournaledAndDetectorResets()
    {
        var h = new Harness();
        h.RunPrelude();
        h.RunDecline();
        h.RunStall();
        h.AskPx = 5000.50m; // spread 2 ticks at signal time → global filter 4
        h.BookEvent(141);
        Assert.Equal(SetupState.Idle, h.Detector.State);
        var cand = Assert.Single(h.Journal.Candidates);
        Assert.Equal(RiskBlock.Spread, cand.Block);
        Assert.Empty(h.Exec.Placed);
        Assert.False(h.Risk.Exposed);
    }

    // ----- entry management -----

    [Fact]
    public void Entry_Escalates_ToMomentumStop_WhenUnfilledAndPriceHolds()
    {
        var h = new Harness();
        h.Arm();
        long limitId = h.Exec.Live()[0].Id;
        h.Trade(Side.Bid, 5000.50m, 5, 172); // 31s after arm, price at L+2
        Assert.Contains(limitId, h.Exec.Cancelled);
        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.StopMarket, spec.Type);
        Assert.Equal(Price.FromDecimal(5000.75m), spec.Price); // L + 3
        // Re-sized for the wider stop: expected fill L+4 vs stop L−3 = 7 ticks → 5 contracts.
        Assert.Equal(5, spec.Quantity);
        Assert.Equal(SetupState.OrderWorking, h.Detector.State);
    }

    [Fact]
    public void Entry_Expires_After90s_ReleasingExposure()
    {
        var h = new Harness();
        h.Arm();
        h.BookEvent(232); // 91s after arm
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Empty(h.Exec.Live());
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Expired, outcome.Disposition);
        Assert.False(h.Risk.Exposed);
        h.BookEvent(233);
        Assert.Equal(SetupState.Idle, h.Detector.State); // Closed → Idle on the next event
    }

    [Fact]
    public void Entry_Cancelled_WhenDefendedLiquidityVanishes()
    {
        var h = new Harness();
        h.Arm();
        h.Bid(15, 145); // 120 → 15 untraded = 87% drop: the absorber pulled
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Empty(h.Exec.Live());
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.CancelledInvalidated, outcome.Disposition);
        Assert.Equal(ExitReason.Invalidation, outcome.ExitReason);
        Assert.False(h.Risk.Exposed);
    }

    // ----- position management -----

    private static (Harness H, long StopId, long T1Id) OpenPosition()
    {
        var h = new Harness();
        h.Arm();
        long entryId = h.Exec.Live()[0].Id;
        h.Fill(entryId, 5000.25m, 10, 142);
        Assert.Equal(SetupState.InPosition, h.Detector.State);
        var live = h.Exec.Live();
        Assert.Equal(2, live.Count);
        var stop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        var t1 = live.Single(o => o.Spec.Type == OrderType.Limit);
        return (h, stop.Id, t1.Id);
    }

    [Fact]
    public void EntryFill_PlacesStopAndT1()
    {
        var (h, stopId, t1Id) = OpenPosition();
        Assert.Equal(Price.FromDecimal(4999.25m), h.Exec.Spec(stopId).Price); // L − 3
        Assert.Equal(10, h.Exec.Spec(stopId).Quantity);
        Assert.Equal(Side.Ask, h.Exec.Spec(stopId).Side);
        Assert.Equal(Price.FromDecimal(5001.25m), h.Exec.Spec(t1Id).Price);  // entry + 1R (R = 4 ticks)
        Assert.Equal(5, h.Exec.Spec(t1Id).Quantity);
    }

    [Fact]
    public void T1Fill_MovesStopToBreakeven_AndPlacesT2AtCap()
    {
        var (h, stopId, t1Id) = OpenPosition();
        h.Fill(t1Id, 5001.25m, 5, 150);
        Assert.Contains(stopId, h.Exec.Cancelled);
        var live = h.Exec.Live();
        var newStop = live.Single(o => o.Spec.Type == OrderType.StopMarket);
        Assert.Equal(Price.FromDecimal(5000.00m), newStop.Spec.Price); // entry − 1 tick
        Assert.Equal(5, newStop.Spec.Quantity);
        // Developing POC (5000.00, the absorbed level) is below entry → T2 = the 3R cap.
        var t2 = live.Single(o => o.Spec.Type == OrderType.Limit);
        Assert.Equal(Price.FromDecimal(5003.25m), t2.Spec.Price); // entry + 12 ticks
        Assert.Equal(5, t2.Spec.Quantity);
        Assert.Equal(SetupState.InPosition, h.Detector.State);
    }

    [Fact]
    public void StopFill_Finalizes_WithNetLoss_MaeMfe_AndStopOutCounted()
    {
        var (h, stopId, t1Id) = OpenPosition();
        h.Trade(Side.Bid, 5000.75m, 5, 145); // MFE 2 ticks
        h.Trade(Side.Ask, 4999.50m, 5, 150); // MAE 3 ticks
        h.Fill(stopId, 4999.00m, 10, 155);   // trigger − 1 tick slippage
        Assert.Equal(SetupState.Closed, h.Detector.State);
        Assert.Contains(t1Id, h.Exec.Cancelled);

        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(CandidateDisposition.Traded, outcome.Disposition);
        Assert.Equal(ExitReason.Stop, outcome.ExitReason);
        Assert.False(outcome.T1Filled);
        Assert.Equal(2, outcome.MfeTicks);
        Assert.Equal(3, outcome.MaeTicks);
        // (4999.00 − 5000.25) = −5 ticks × $12.50 × 10 = −$625 gross, −$14 commission.
        Assert.Equal(-625m, outcome.GrossPnl);
        Assert.Equal(14m, outcome.Commission);
        Assert.Equal(-639m, outcome.NetPnl);
        Assert.False(h.Risk.Exposed);
    }

    [Fact]
    public void TimeStop_ExitsAtMarket_WhenNeitherT1NorStopHitIn5Minutes()
    {
        var (h, stopId, t1Id) = OpenPosition();
        h.BookEvent(443); // 301s after the 142s entry
        Assert.Contains(stopId, h.Exec.Cancelled);
        Assert.Contains(t1Id, h.Exec.Cancelled);
        var (exitId, exitSpec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exitSpec.Type);
        Assert.Equal(10, exitSpec.Quantity);
        h.Fill(exitId, 5000.00m, 10, 443.5);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.TimeStop, outcome.ExitReason);
        Assert.Equal(SetupState.Closed, h.Detector.State);
    }

    [Fact]
    public void SweepThroughLevel_InvalidatesPosition_Immediately()
    {
        var (h, _, _) = OpenPosition();
        h.Trade(Side.Ask, 4999.75m, 200, 145); // ≥ 200 contracts strictly through L
        var exit = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exit.Spec.Type);
        h.Fill(exit.Id, 4999.50m, 10, 145.5);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.Invalidation, outcome.ExitReason);
    }

    [Fact]
    public void SmallPrintThroughLevel_DoesNotInvalidate()
    {
        var (h, _, _) = OpenPosition();
        h.Trade(Side.Ask, 4999.75m, 199, 145);
        Assert.Equal(SetupState.InPosition, h.Detector.State);
        Assert.Equal(2, h.Exec.Live().Count); // stop + T1 untouched
    }

    [Fact]
    public void RthEnd_FlattensPosition()
    {
        var (h, _, _) = OpenPosition();
        // 20:00:30 UTC = 16:00:30 ET, past the close.
        double t = (6 * 3600 + 30) / 1.0;
        h.BookEvent(t);
        var exit = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Market, exit.Spec.Type);
        h.Fill(exit.Id, 5000.00m, 10, t + 1);
        var outcome = Assert.Single(h.Journal.Outcomes);
        Assert.Equal(ExitReason.SessionEnd, outcome.ExitReason);
    }

    // ----- short mirror -----

    [Fact]
    public void ShortMirror_ArmsWithSellLimitBelowLevel()
    {
        var h = new Harness(direction: TradeDirection.Short);
        h.BidPx = 4999.75m;
        h.AskPx = 5000.00m;
        h.AskSize = 100;
        // Prelude well below the 5000 LOI.
        for (int i = 0; i < 6; i++)
        {
            h.Trade(Side.Ask, 4998.50m - i * 0.25m, 10, i * 5);
        }
        h.Trade(Side.Ask, 4998.50m, 5, 61);
        // Rally into L: 6 buy prints of 60.
        decimal[] prices = { 4998.50m, 4998.75m, 4999.00m, 4999.25m, 4999.50m, 4999.75m };
        for (int i = 0; i < prices.Length; i++)
        {
            h.Trade(Side.Bid, prices[i], 60, 65 + i * 5);
        }
        Assert.Equal(SetupState.ContextMet, h.Detector.State);
        // Stall: buyers hitting the 5000.00 offer, decaying buckets, ask refreshing 3×.
        void Ask(uint size, double t)
        {
            h.AskSize = size;
            h.BookEvent(t);
        }
        h.Trade(Side.Bid, 5000.00m, 80, 91);
        Ask(40, 92);
        Ask(100, 93);
        h.Trade(Side.Bid, 5000.00m, 70, 95);
        h.Trade(Side.Bid, 5000.00m, 50, 101);
        Ask(35, 103);
        Ask(110, 104);
        h.Trade(Side.Bid, 5000.00m, 30, 105);
        h.Trade(Side.Bid, 5000.00m, 60, 111);
        Ask(30, 113);
        Ask(120, 114);
        h.Trade(Side.Bid, 5000.00m, 25, 121);
        h.Trade(Side.Bid, 5000.00m, 10, 131);
        h.Trade(Side.Ask, 4999.75m, 12, 135);
        h.BookEvent(141);

        Assert.Equal(SetupState.OrderWorking, h.Detector.State);
        var (_, spec) = Assert.Single(h.Exec.Live());
        Assert.Equal(OrderType.Limit, spec.Type);
        Assert.Equal(Side.Ask, spec.Side);
        Assert.Equal(Price.FromDecimal(4999.75m), spec.Price); // L − 1 tick
    }
}
