using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 1 — absorption fade. Long version per the rulebook; the short instance mirrors
/// through <see cref="SetupDetectorBase.Sign"/>. Transitions (each rulebook condition is
/// a guard in <see cref="AbsorptionGuards"/>):
///
///   Idle → ContextMet      A1 (decline into an LOI) + A2 (aggressive flow), gated on the
///                          session delta distribution being ready (CLAUDE.md).
///   ContextMet → Armed     A3 stall + A4 volume-without-progress + A5 replenishment +
///                          A6 exhaustion, then global filters 1–6; blocked candidates
///                          are journaled and the machine re-stalls from Idle.
///   Armed → OrderWorking   buy limit at L+1; after 30 s unfilled with price ≥ L+2 the
///                          entry escalates to a buy stop at L+3 (re-sized for the wider
///                          stop so filter 6 still holds); 90 s unfilled = expired.
///   OrderWorking/InPosition invalidations: the defended level's displayed size vanishes
///                          untraded (> 80% since arm), or a single print / same-instant
///                          sweep ≥ 200 trades strictly through L.
///   InPosition             stop at L−3; T1 at entry+1R exits 50% and moves the stop to
///                          entry−1; T2 = developing POC capped at entry+3R (VWAP and
///                          "prior bounce high" are not computed in v1 — documented
///                          ambiguity); time stop 5 min until T1 or stop; flat at RTH end.
///
/// MBP-10 approximations (documented per CLAUDE.md): A5's "refreshed to ≥ 60% of pre-hit
/// size" is approximated by the liquidity tracker's refresh count (any displayed-size
/// increase at the populated level) over its 3-minute window rather than the exact stall
/// window; the stall itself starts when context is first met.
/// </summary>
public sealed class AbsorptionFadeDetector : SetupDetectorBase
{
    private readonly Setup1Options _o;
    private readonly MonotonicWindowExtreme _priceExtreme;
    private int _deltaWindowIdx = -1;
    private Price _lastTradePrice = Price.Undefined;

    // ----- stall accumulators (ContextMet) -----
    private Timestamp _stallStart;
    private long _stallAggressorPrints;
    private long _stallVolumeAtLevel;
    private readonly List<long> _bucketVolume = new();
    private readonly List<long> _bucketFavorableDelta = new();

    // ----- entry (OrderWorking) -----
    private long _entryOrderId = -1;
    private Timestamp _armTs;
    private bool _escalated;
    private long _qty;

    // ----- position orders -----
    private long _stopOrderId = -1;
    private long _t1OrderId = -1;
    private long _t2OrderId = -1;
    private long _exitOrderId = -1;
    private long _rTicks;
    private Timestamp _entryTs;

    // ----- level liquidity + sweep tracking since arm -----
    private long _maxDisplayedSinceArm;
    private long _tradedAtLevelSinceArm;
    private Timestamp _sweepTs;
    private long _sweepVolumeThroughLevel;

    /// <summary>Funnel counters for calibration/diagnostics (printed by the backtest CLI):
    /// how often context was entered, how far the signal-guard chain got, and the longest
    /// stall achieved. Not used by any trading logic.</summary>
    public struct FunnelCounters
    {
        public long ContextEntered;
        public long ResetDefendedGaveWay;
        public long ResetPriceRanAway;
        public long A3Passed;
        public long A4Passed;
        public long A5Passed;
        public long Candidates;
        public long MaxStallNanos;
    }

    public FunnelCounters Funnel;

    public override string? FunnelLine()
    {
        var f = Funnel;
        return $"context {f.ContextEntered:N0} (gave-way {f.ResetDefendedGaveWay:N0}, ran-away {f.ResetPriceRanAway:N0}), " +
               $"max stall {f.MaxStallNanos / 1_000_000_000.0:F1}s, " +
               $"A3 {f.A3Passed:N0} → A4 {f.A4Passed:N0} → A5 {f.A5Passed:N0} → candidates {f.Candidates:N0}";
    }

    public AbsorptionFadeDetector(
        TickSize tick,
        Setup1Options options,
        TradeDirection direction,
        RiskManager risk,
        IExecutionPort exec,
        ICandidateJournal journal,
        ExecutionOptions execOptions,
        RiskOptions riskOptions,
        Func<long> nextCandidateId)
        : base(SetupId.AbsorptionFade, direction, tick, risk, exec, journal, execOptions, riskOptions, nextCandidateId)
    {
        _o = options;
        // Long fades a decline: the adverse extreme is the rolling high. Short mirrors.
        _priceExtreme = new MonotonicWindowExtreme(options.DeclineWindowSeconds, trackMax: direction == TradeDirection.Long);
    }

    private Side DefendedSide => Direction == TradeDirection.Long ? Side.Bid : Side.Ask;

    private Side AggressorSide => Direction == TradeDirection.Long ? Side.Ask : Side.Bid;

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
            _priceExtreme.Add(e.TsEvent, e.Price.RawNano);
        }
        else
        {
            _priceExtreme.Advance(e.TsEvent);
        }

        switch (State)
        {
            case SetupState.Idle when e.Kind == MarketEventKind.Trade:
                TryEnterContext(in e, features);
                break;
            case SetupState.ContextMet:
                StepContext(in e, tracker, features);
                break;
            case SetupState.OrderWorking:
                StepOrderWorking(in e, tracker);
                break;
            case SetupState.InPosition:
                StepInPosition(in e, tracker);
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
        _stallAggressorPrints = 0;
        _stallVolumeAtLevel = 0;
        _bucketVolume.Clear();
        _bucketFavorableDelta.Clear();
        _entryOrderId = -1;
        _stopOrderId = -1;
        _t1OrderId = -1;
        _t2OrderId = -1;
        _exitOrderId = -1;
        _escalated = false;
        _qty = 0;
        _maxDisplayedSinceArm = 0;
        _tradedAtLevelSinceArm = 0;
        _sweepVolumeThroughLevel = 0;
    }

    // ----- Idle → ContextMet -----

    private void TryEnterContext(in MarketEvent e, FeatureEngine features)
    {
        long declineTicks = _priceExtreme.Extreme is { } extreme
            ? Sign * (extreme - e.Price.RawNano) / Tick.RawNano
            : 0;
        if (!features.TryGetNearestLoi(e.Price, out var loi))
        {
            return;
        }
        if (!AbsorptionGuards.A1_DeclineIntoLevel(declineTicks, loi.SignedDistanceTicks, _o))
        {
            return;
        }

        if (_deltaWindowIdx < 0)
        {
            _deltaWindowIdx = features.FlowWindowIndex(_o.DeltaWindowSeconds);
        }
        if (!features.DeltaDistributionReady(_deltaWindowIdx))
        {
            return; // CLAUDE.md: no arming before the session baseline is ready
        }
        long delta = features.WindowDelta(_deltaWindowIdx);
        double? rank = features.DeltaPercentileRank(_deltaWindowIdx, delta);
        double? tailRank = rank is { } r ? (Direction == TradeDirection.Long ? r : 1 - r) : null;
        if (!AbsorptionGuards.A2_AggressiveFlow(Sign * delta, tailRank, _o))
        {
            return;
        }

        Level = loi.Price;
        _stallStart = e.TsEvent;
        _stallAggressorPrints = 0;
        _stallVolumeAtLevel = 0;
        _bucketVolume.Clear();
        _bucketFavorableDelta.Clear();
        Funnel.ContextEntered++;
        State = SetupState.ContextMet;
    }

    // ----- ContextMet → Armed → OrderWorking -----

    private void StepContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        // Reset guards: the defended best gave way, or price ran away from the level.
        if (!tracker.TryGetBest(DefendedSide, out var best)
            || Sign * (best.Price.RawNano - Level.RawNano) < 0)
        {
            Funnel.ResetDefendedGaveWay++;
            ResetToIdle();
            return;
        }
        if (e.Kind == MarketEventKind.Trade
            && Sign * (e.Price.RawNano - Level.RawNano) / Tick.RawNano > _o.StallAbandonTicks)
        {
            Funnel.ResetPriceRanAway++;
            ResetToIdle();
            return;
        }

        // Stall accumulators (bucket 10s grid is anchored at the stall start).
        long stallNanos = (long)(e.TsEvent.UnixNanos - _stallStart.UnixNanos);
        long bucketNanos = _o.ExhaustionBucketSeconds * 1_000_000_000L;
        if (e.Kind == MarketEventKind.Trade && e.Side != Side.None)
        {
            int bucket = (int)(stallNanos / bucketNanos);
            while (_bucketVolume.Count <= bucket)
            {
                _bucketVolume.Add(0);
                _bucketFavorableDelta.Add(0);
            }
            _bucketFavorableDelta[bucket] += Sign * (e.Side == Side.Bid ? e.Size : -(long)e.Size);
            if (e.Side == AggressorSide)
            {
                _stallAggressorPrints++;
                _bucketVolume[bucket] += e.Size;
                long offsetTicks = Sign * (e.Price.RawNano - Level.RawNano) / Tick.RawNano;
                if (offsetTicks is 0 or 1) // [L, L+1] toward the entry side
                {
                    _stallVolumeAtLevel += e.Size;
                }
            }
        }

        // Signal conditions A3–A6, all required.
        Funnel.MaxStallNanos = Math.Max(Funnel.MaxStallNanos, stallNanos);
        if (!AbsorptionGuards.A3_PriceStalls(stallNanos, _stallAggressorPrints, _o))
        {
            return;
        }
        Funnel.A3Passed++;
        if (!AbsorptionGuards.A4_VolumeWithoutProgress(_stallVolumeAtLevel, features.BaselinePerPriceVolume, _o))
        {
            return;
        }
        Funnel.A4Passed++;
        if (!AbsorptionGuards.A5_Replenishment(
                features.Liquidity.ReplenishRatio(DefendedSide, Level, e.TsEvent),
                features.Liquidity.RefreshCount(DefendedSide, Level, e.TsEvent), _o))
        {
            return;
        }
        Funnel.A5Passed++;
        int completedBuckets = (int)(stallNanos / bucketNanos);
        long peak = 0;
        for (int i = 0; i < completedBuckets && i < _bucketVolume.Count; i++)
        {
            peak = Math.Max(peak, _bucketVolume[i]);
        }
        int last = completedBuckets - 1;
        long lastVolume = last >= 0 && last < _bucketVolume.Count ? _bucketVolume[last] : 0;
        long lastDelta = last >= 0 && last < _bucketFavorableDelta.Count ? _bucketFavorableDelta[last] : 0;
        if (!AbsorptionGuards.A6_Exhaustion(completedBuckets, peak, lastVolume, lastDelta, _o))
        {
            return;
        }

        // Candidate: global filters + journal (blocked candidates re-stall from Idle).
        Funnel.Candidates++;
        long stopDistanceTicks = _o.EntryLimitOffsetTicks + _o.StopOffsetTicks;
        var verdict = EmitCandidate(in e, tracker, features, _lastTradePrice, stopDistanceTicks);
        if (!verdict.Approved)
        {
            ResetToIdle();
            return;
        }
        State = SetupState.Armed;
        Risk.Reserve(Level.RawNano);
        _qty = verdict.Quantity;
        _entryOrderId = Exec.Place(new OrderSpec(
            EntrySide, OrderType.Limit, Level.AddTicks(Sign * _o.EntryLimitOffsetTicks, Tick), _qty));
        _armTs = e.TsEvent;
        _escalated = false;
        _maxDisplayedSinceArm = tracker.DisplayedAt(DefendedSide, Level);
        _tradedAtLevelSinceArm = 0;
        _sweepVolumeThroughLevel = 0;
        State = SetupState.OrderWorking;
    }

    // ----- OrderWorking -----

    private void StepOrderWorking(in MarketEvent e, BookStateTracker tracker)
    {
        long displayed = TrackLevelLiquidity(in e, tracker);
        if (AbsorptionGuards.LevelVanished(_maxDisplayedSinceArm, displayed, _tradedAtLevelSinceArm, _o)
            || AbsorptionGuards.SweepInvalidation(_sweepVolumeThroughLevel, _o))
        {
            CancelOrder(ref _entryOrderId);
            FinalizeNoTrade(CandidateDisposition.CancelledInvalidated, ExitReason.Invalidation);
            return;
        }

        long sinceArm = (long)(e.TsEvent.UnixNanos - _armTs.UnixNanos);
        if (AbsorptionGuards.EntryExpired(sinceArm, _o))
        {
            CancelOrder(ref _entryOrderId);
            FinalizeNoTrade(CandidateDisposition.Expired, ExitReason.None);
            return;
        }
        if (!_escalated && !_lastTradePrice.IsUndefined)
        {
            long advanceTicks = Sign * (_lastTradePrice.RawNano - Level.RawNano) / Tick.RawNano;
            if (AbsorptionGuards.ShouldEscalateToMomentum(sinceArm, advanceTicks, _o))
            {
                CancelOrder(ref _entryOrderId);
                // Momentum entry risks more ticks (expected fill = trigger + slippage), so
                // re-size to keep filter 6's risk budget honest.
                long stopDistanceTicks = _o.MomentumStopOffsetTicks + ExecOpts.StopSlippageTicks + _o.StopOffsetTicks;
                long qty = Math.Min(_qty, Risk.Size(stopDistanceTicks));
                if (qty <= 0)
                {
                    FinalizeNoTrade(CandidateDisposition.Expired, ExitReason.None);
                    return;
                }
                _qty = qty;
                _entryOrderId = Exec.Place(new OrderSpec(
                    EntrySide, OrderType.StopMarket, Level.AddTicks(Sign * _o.MomentumStopOffsetTicks, Tick), _qty));
                _escalated = true;
            }
        }
    }

    // ----- InPosition -----

    private void StepInPosition(in MarketEvent e, BookStateTracker tracker)
    {
        if (e.Kind == MarketEventKind.Trade)
        {
            UpdateExcursions(e.Price);
        }
        long displayed = TrackLevelLiquidity(in e, tracker);
        if (_exitOrderId >= 0)
        {
            return; // market exit pending — waiting for its fill
        }
        if (AbsorptionGuards.LevelVanished(_maxDisplayedSinceArm, displayed, _tradedAtLevelSinceArm, _o)
            || AbsorptionGuards.SweepInvalidation(_sweepVolumeThroughLevel, _o))
        {
            ExitAtMarket(ExitReason.Invalidation);
            return;
        }
        long sinceEntry = (long)(e.TsEvent.UnixNanos - _entryTs.UnixNanos);
        if (AbsorptionGuards.TimeStopHit(sinceEntry, T1Filled, _o))
        {
            ExitAtMarket(ExitReason.TimeStop);
        }
    }

    /// <summary>Per-event upkeep of the defended level's displayed size, traded volume and
    /// the same-instant sweep volume through L; returns the current displayed size.</summary>
    private long TrackLevelLiquidity(in MarketEvent e, BookStateTracker tracker)
    {
        if (e.Kind == MarketEventKind.Trade)
        {
            if (e.Price == Level)
            {
                _tradedAtLevelSinceArm += e.Size;
            }
            if (e.Side == AggressorSide)
            {
                if (e.TsEvent != _sweepTs)
                {
                    _sweepTs = e.TsEvent;
                    _sweepVolumeThroughLevel = 0;
                }
                if (Sign * (e.Price.RawNano - Level.RawNano) < 0) // strictly through L
                {
                    _sweepVolumeThroughLevel += e.Size;
                }
            }
        }
        long displayed = tracker.DisplayedAt(DefendedSide, Level);
        _maxDisplayedSinceArm = Math.Max(_maxDisplayedSinceArm, displayed);
        return displayed;
    }

    private void ExitAtMarket(ExitReason reason)
    {
        CancelOrder(ref _stopOrderId);
        CancelOrder(ref _t1OrderId);
        CancelOrder(ref _t2OrderId);
        PendingExitReason = reason;
        _exitOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.Market, Price.Undefined, Remaining));
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
                FinalizeTrade(ExitReason.Target); // 1-lot position: T1 was the whole exit
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
        var stopPrice = Level.AddTicks(-Sign * _o.StopOffsetTicks, Tick);
        _rTicks = Sign * (fill.Price.RawNano - stopPrice.RawNano) / Tick.RawNano;
        _stopOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.StopMarket, stopPrice, Remaining));
        long t1Ticks = (long)Math.Round(_o.T1RMultiple * _rTicks, MidpointRounding.AwayFromZero);
        long t1Qty = Math.Clamp((long)Math.Floor(fill.Quantity * _o.T1ExitFraction), 1, fill.Quantity);
        _t1OrderId = Exec.Place(new OrderSpec(
            ExitSide, OrderType.Limit, fill.Price.AddTicks(Sign * t1Ticks, Tick), t1Qty));
    }

    /// <summary>T2: nearest opposing structure capped at T2RCap × R. v1 computes the
    /// developing session POC; VWAP and "prior bounce high" are not available (documented
    /// rulebook ambiguity) — without a POC beyond entry, T2 is the cap.</summary>
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
