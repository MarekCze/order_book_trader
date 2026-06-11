using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 5 — delta divergence fade. Stated for the short (divergence at a new high); the long
/// mirror fades a new low through <see cref="SetupDetectorBase.Sign"/>. The detector reads the
/// engine's high-side swing divergence for the short and the low-side for the long. Transitions
/// (each rulebook condition is a guard in <see cref="Setup5Guards"/>):
///
///   Idle → ContextMet      a fresh new 30-minute extreme this event, E1 (beyond the prior swing
///                          by ≥ 2 ticks) + E2 (cumDelta divergence, or a non-confirming bar
///                          closing in the extreme third) + E3 (within 4 ticks of an LOI).
///   ContextMet → Armed     E4 (a diagonal imbalance within 2 ticks of the extreme, or price
///                          reclaiming past the prior swing), then global filters 1–6; a fresher
///                          new extreme re-arms context, and the context expires after a bound.
///   Armed → OrderWorking   sell limit 2 ticks inside the extreme; cancelled if unfilled in 2 min.
///   InPosition             stop 2 ticks beyond the extreme; T1 at entry + 1R (50% off, stop to
///                          entry); T2 = developing POC capped at 3R; invalidation when a 10-second
///                          bucket prints ≥ 150 of aggressor delta the adverse way beyond the prior
///                          swing; flat at RTH end. No time stop (the rulebook gives none).
///
/// MBP-10 / v1 approximations (documented per CLAUDE.md): T2's "session VWAP or developing POC"
/// uses the developing POC only (VWAP is not computed); the "30-minute high H2 / prior swing high
/// H1" are the engine's rolling-swing divergence samples.
/// </summary>
public sealed class DeltaDivergenceFadeDetector : SetupDetectorBase
{
    private readonly Setup5Options _o;
    private Price _lastTradePrice = Price.Undefined;

    private Price _h1 = Price.Undefined; // prior swing extreme
    private Price _h2 = Price.Undefined; // new extreme being faded
    private Timestamp _contextTs;

    private long _entryOrderId = -1;
    private long _stopOrderId = -1;
    private long _t1OrderId = -1;
    private long _t2OrderId = -1;
    private long _exitOrderId = -1;
    private Timestamp _armTs;
    private Timestamp _entryTs;
    private long _rTicks;

    // invalidation bucket (10s grid anchored at entry)
    private long _bucketIndex = long.MinValue;
    private long _bucketAdverseDelta;

    private long _contextEntered;
    private long _candidates;

    private const int E1 = 0, E2 = 1, E3 = 2, E4 = 3;

    /// <summary>Per-condition funnel telemetry (diagnostic only). E1's evaluated count is
    /// the number of fresh new-30-min-extreme samples seen while Idle; E4's is the events
    /// spent waiting in ContextMet.</summary>
    public ConditionFunnel Conditions { get; } = new("E1", "E2", "E3", "E4");

    public override string? FunnelLine() =>
        $"context {_contextEntered:N0} → candidates {_candidates:N0} | {Conditions.Summary()}";

    public DeltaDivergenceFadeDetector(
        TickSize tick,
        Setup5Options options,
        TradeDirection direction,
        RiskManager risk,
        IExecutionPort exec,
        ICandidateJournal journal,
        ExecutionOptions execOptions,
        RiskOptions riskOptions,
        Func<long> nextCandidateId)
        : base(SetupId.DeltaDivergenceFade, direction, tick, risk, exec, journal, execOptions, riskOptions, nextCandidateId)
    {
        _o = options;
    }

    private Side EntrySide => Direction == TradeDirection.Long ? Side.Bid : Side.Ask;

    private Side ExitSide => Direction == TradeDirection.Long ? Side.Ask : Side.Bid;

    protected override void Step(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        if (!_o.Enabled)
        {
            return;
        }
        if (e.Kind == MarketEventKind.Trade)
        {
            _lastTradePrice = e.Price;
        }

        switch (State)
        {
            case SetupState.Idle:
                TryEnterContext(in e, tracker, features);
                break;
            case SetupState.ContextMet:
                StepContext(in e, tracker, features);
                break;
            case SetupState.OrderWorking:
                StepOrderWorking(in e);
                break;
            case SetupState.InPosition:
                StepInPosition(in e);
                break;
        }
    }

    protected override void OnOutsideRth(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        switch (State)
        {
            case SetupState.InPosition when _exitOrderId < 0:
                ExitAtMarket(ExitReason.SessionEnd);
                break;
            case SetupState.OrderWorking:
                CancelOrder(ref _entryOrderId);
                FinalizeNoTrade(CandidateDisposition.Expired, ExitReason.SessionEnd);
                break;
            case SetupState.ContextMet:
                ResetToIdle();
                break;
        }
    }

    protected override void OnReset()
    {
        _h1 = Price.Undefined;
        _h2 = Price.Undefined;
        _entryOrderId = -1;
        _stopOrderId = -1;
        _t1OrderId = -1;
        _t2OrderId = -1;
        _exitOrderId = -1;
        _bucketIndex = long.MinValue;
        _bucketAdverseDelta = 0;
    }

    // ----- Idle → ContextMet -----

    private void TryEnterContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        var sample = Direction == TradeDirection.Short ? features.LastHighDivergence : features.LastLowDivergence;
        if (sample is not { } d || d.Ts != e.TsEvent)
        {
            return; // only act on a fresh new extreme this event
        }

        long extensionTicks = Direction == TradeDirection.Short
            ? d.NewExtreme.TicksFrom(d.PriorExtreme, Tick)
            : d.PriorExtreme.TicksFrom(d.NewExtreme, Tick);
        if (!Conditions.Check(E1, Setup5Guards.E1_NewExtremeBeyondPrior(extensionTicks, _o)))
        {
            return;
        }

        long cumDeltaGap = Sign * (d.CumDeltaNew - d.CumDeltaPrior);
        var bar = features.FormingBar;
        bool barDeltaNonConfirming = Sign * bar.Delta >= 0;
        double directionalRangePos = DirectionalRangePos(bar);
        if (!Conditions.Check(E2, Setup5Guards.E2_Divergence(cumDeltaGap, barDeltaNonConfirming, directionalRangePos, _o)))
        {
            return;
        }

        long? loiDistance = features.TryGetNearestLoi(d.NewExtreme, out var loi) ? loi.SignedDistanceTicks : null;
        if (!Conditions.Check(E3, Setup5Guards.E3_Location(loiDistance, _o)))
        {
            return;
        }

        _h1 = d.PriorExtreme;
        _h2 = d.NewExtreme;
        Level = _h2;
        _contextTs = e.TsEvent;
        _contextEntered++;
        SampleVolatilityRegime(features);
        State = SetupState.ContextMet;
        TryTrigger(in e, tracker, features); // E4 may already hold this event
    }

    // ----- ContextMet → Armed -----

    private void StepContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        // A fresher new extreme supersedes this context — re-evaluate from scratch.
        var sample = Direction == TradeDirection.Short ? features.LastHighDivergence : features.LastLowDivergence;
        if (sample is { } d && d.Ts == e.TsEvent && d.NewExtreme != _h2)
        {
            ResetToIdle();
            TryEnterContext(in e, tracker, features);
            return;
        }
        if ((long)(e.TsEvent.UnixNanos - _contextTs.UnixNanos) >= _o.ContextExpirySeconds * 1_000_000_000L)
        {
            ResetToIdle();
            return;
        }
        TryTrigger(in e, tracker, features);
    }

    private void TryTrigger(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        bool imbalance = Direction == TradeDirection.Short
            ? FootprintFeatures.HasSellImbalanceNear(features.FormingBar, Tick, _h2, _o.ImbalanceProximityTicks, _o.ImbalanceRatio)
            : FootprintFeatures.HasBuyImbalanceNear(features.FormingBar, Tick, _h2, _o.ImbalanceProximityTicks, _o.ImbalanceRatio);
        bool reclaimed = !_lastTradePrice.IsUndefined && (Direction == TradeDirection.Short
            ? _lastTradePrice.RawNano < _h1.RawNano
            : _lastTradePrice.RawNano > _h1.RawNano);
        if (!Conditions.Check(E4, Setup5Guards.E4_Trigger(imbalance, reclaimed)))
        {
            return;
        }

        // Candidate: global filters keyed on the extreme's location + journal.
        _candidates++;
        long stopDistanceTicks = _o.EntryOffsetTicks + _o.StopOffsetTicks;
        var verdict = EmitCandidate(in e, tracker, features, _h2, stopDistanceTicks);
        if (!verdict.Approved)
        {
            ResetToIdle();
            return;
        }
        State = SetupState.Armed;
        Risk.Reserve(Level.RawNano);
        _entryOrderId = Exec.Place(new OrderSpec(
            EntrySide, OrderType.Limit, _h2.AddTicks(Sign * _o.EntryOffsetTicks, Tick), verdict.Quantity));
        _armTs = e.TsEvent;
        State = SetupState.OrderWorking;
    }

    // ----- OrderWorking -----

    private void StepOrderWorking(in MarketEvent e)
    {
        if ((long)(e.TsEvent.UnixNanos - _armTs.UnixNanos) >= _o.EntryExpirySeconds * 1_000_000_000L)
        {
            CancelOrder(ref _entryOrderId);
            FinalizeNoTrade(CandidateDisposition.Expired, ExitReason.None);
        }
    }

    // ----- InPosition -----

    private void StepInPosition(in MarketEvent e)
    {
        if (e.Kind == MarketEventKind.Trade)
        {
            UpdateExcursions(e.Price);
        }
        if (_exitOrderId >= 0)
        {
            return; // market exit pending
        }
        if (e.Kind == MarketEventKind.Trade && e.Side != Side.None && AccumulateAdverseBucket(in e))
        {
            ExitAtMarket(ExitReason.Invalidation);
        }
    }

    /// <summary>Adds aggressor delta printed the adverse way beyond the prior swing into the
    /// current 10-second bucket; returns true when it crosses the invalidation threshold.</summary>
    private bool AccumulateAdverseBucket(in MarketEvent e)
    {
        long bucketNanos = _o.DeltaBucketSeconds * 1_000_000_000L;
        long bucket = (long)(e.TsEvent.UnixNanos - _entryTs.UnixNanos) / bucketNanos;
        if (bucket != _bucketIndex)
        {
            _bucketIndex = bucket;
            _bucketAdverseDelta = 0;
        }
        bool beyondPriorSwing = Direction == TradeDirection.Short
            ? e.Price.RawNano > _h1.RawNano
            : e.Price.RawNano < _h1.RawNano;
        if (beyondPriorSwing)
        {
            long signedSize = e.Side == Side.Bid ? e.Size : -(long)e.Size;
            _bucketAdverseDelta += -Sign * signedSize; // adverse = against the fade
        }
        return Setup5Guards.Invalidation_RealAggressors(_bucketAdverseDelta, _o);
    }

    private void ExitAtMarket(ExitReason reason)
    {
        CancelOrder(ref _stopOrderId);
        CancelOrder(ref _t1OrderId);
        CancelOrder(ref _t2OrderId);
        PendingExitReason = reason;
        _exitOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.Market, Price.Undefined, Remaining));
    }

    /// <summary>Close position toward the extreme: 1 = close at the extreme, 0 = at the far end of
    /// the bar's range. Empty/degenerate bars read as "at the extreme".</summary>
    private double DirectionalRangePos(FootprintBar bar)
    {
        if (bar.Open.IsUndefined || bar.High == bar.Low)
        {
            return 1.0;
        }
        double span = bar.High.RawNano - bar.Low.RawNano;
        double fromLow = (bar.Close.RawNano - bar.Low.RawNano) / span;
        return Direction == TradeDirection.Short ? fromLow : 1.0 - fromLow;
    }

    // ----- fills -----

    public override void OnFill(in Fill fill, FeatureEngine features)
    {
        if (fill.OrderId == _entryOrderId)
        {
            HandleEntryFill(in fill);
            return;
        }

        ExitFills.Add(fill);
        Remaining -= fill.Quantity;

        if (fill.OrderId == _t1OrderId)
        {
            _t1OrderId = -1;
            T1Filled = true;
            CancelOrder(ref _stopOrderId);
            if (Remaining <= 0)
            {
                FinalizeTrade(ExitReason.Target);
                return;
            }
            _stopOrderId = Exec.Place(new OrderSpec(
                ExitSide, OrderType.StopMarket,
                EntryFill.Price.AddTicks(-Sign * _o.BreakevenOffsetTicks, Tick), Remaining));
            _t2OrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.Limit, ComputeT2(features), Remaining));
        }
        else if (fill.OrderId == _stopOrderId)
        {
            _stopOrderId = -1;
            CancelOrder(ref _t1OrderId);
            CancelOrder(ref _t2OrderId);
            FinalizeTrade(ExitReason.Stop);
        }
        else if (fill.OrderId == _t2OrderId)
        {
            _t2OrderId = -1;
            CancelOrder(ref _stopOrderId);
            FinalizeTrade(ExitReason.Target);
        }
        else if (fill.OrderId == _exitOrderId && Remaining <= 0)
        {
            _exitOrderId = -1;
            FinalizeTrade(PendingExitReason);
        }
    }

    private void HandleEntryFill(in Fill fill)
    {
        _entryOrderId = -1;
        OpenPosition(in fill);
        _entryTs = fill.Ts;
        _bucketIndex = long.MinValue;
        _bucketAdverseDelta = 0;
        var stopPrice = _h2.AddTicks(-Sign * _o.StopOffsetTicks, Tick);
        _rTicks = Sign * (fill.Price.RawNano - stopPrice.RawNano) / Tick.RawNano;
        _stopOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.StopMarket, stopPrice, Remaining));
        long t1Ticks = (long)Math.Round(_o.T1RMultiple * _rTicks, MidpointRounding.AwayFromZero);
        long t1Qty = Math.Clamp((long)Math.Floor(fill.Quantity * _o.T1ExitFraction), 1, fill.Quantity);
        _t1OrderId = Exec.Place(new OrderSpec(
            ExitSide, OrderType.Limit, fill.Price.AddTicks(Sign * t1Ticks, Tick), t1Qty));
    }

    /// <summary>T2: developing POC in the profit direction, capped at T2RCap × R (VWAP and the
    /// rulebook's other structures are not computed in v1 — documented).</summary>
    private Price ComputeT2(FeatureEngine features)
    {
        long capTicks = (long)Math.Round(_o.T2RCap * _rTicks, MidpointRounding.AwayFromZero);
        long t2Ticks = capTicks;
        if (features.DevelopingPoc is { } poc)
        {
            long pocTicks = Sign * (poc.RawNano - EntryFill.Price.RawNano) / Tick.RawNano;
            if (pocTicks > 0)
            {
                t2Ticks = Math.Min(t2Ticks, pocTicks);
            }
        }
        return EntryFill.Price.AddTicks(Sign * t2Ticks, Tick);
    }
}
