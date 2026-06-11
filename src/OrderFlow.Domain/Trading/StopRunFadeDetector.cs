using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 2 — stop-run fade (trapped traders). Stated for the short (fade a swept high); the long
/// mirror fades a swept low through <see cref="SetupDetectorBase.Sign"/>. Transitions (each
/// rulebook condition is a guard in <see cref="Setup2Guards"/>):
///
///   Idle → ContextMet      B1 (a reference high/low — PDH/ONH — exists) + B2 (price swept it by
///                          1–5 ticks) + B3 (the breaking bar was a climax: ≥ 90th-percentile
///                          aggressive volume, ≥ 60% executed at/beyond the level). The sweep high
///                          is captured and frozen once price first pulls back.
///   ContextMet → Armed     B4 (no follow-through: no new high beyond the sweep by > 1 tick) holds
///                          and B5 (supply confirms: offers stacking ≥ 50%, or ≥ 3 diagonal
///                          imbalances), then global filters 1–6.
///   Armed → OrderWorking   sell stop 2 ticks inside the level; its trigger realizes B6 (price
///                          reclaiming past the level). Cancelled if not triggered within 4 min of
///                          the sweep, or on a breakout.
///   InPosition             stop 1 tick beyond the sweep high; T1 at entry + 1R (50% off, stop to
///                          entry); T2 = developing POC capped at 4R; scratch when price holds back
///                          past the level for 60 s; flat at RTH end.
///
/// MBP-10 / v1 approximations (documented per CLAUDE.md): B1 uses PDH/ONH only (the "swing high
/// ≥ 30 min old" needs pivot-age tracking, deferred); B5's stacking is read from F21 (levels
/// beyond the best) rather than "the 3 levels above the sweep" exactly; T2's "origin of the
/// breakout leg" uses the developing POC.
/// </summary>
public sealed class StopRunFadeDetector : SetupDetectorBase
{
    private readonly Setup2Options _o;

    private Price _h = Price.Undefined;     // reference level swept
    private Price _sweepExtreme = Price.Undefined;
    private Timestamp _sweepTs;
    private bool _sweepFrozen;
    private long _maxBeyondSweepTicks;

    private long _entryOrderId = -1;
    private long _stopOrderId = -1;
    private long _t1OrderId = -1;
    private long _t2OrderId = -1;
    private long _exitOrderId = -1;
    private Timestamp _entryTs;
    private long _rTicks;

    // scratch: time price has continuously held back past the level
    private bool _heldBackPastLevel;
    private Timestamp _heldBackSince;

    private long _contextEntered;
    private long _candidates;

    private const int B1B2 = 0, B3 = 1, B4 = 2, B5 = 3;

    /// <summary>Per-condition funnel telemetry (diagnostic only). B1B2 is the swept-reference
    /// query (a reference level exists AND price poked it by 1–5 ticks), evaluated per trade
    /// event while Idle; B4/B5 are evaluated per event while ContextMet (the pre-candidate
    /// chain only — the post-arm B4 cancel check is not counted).</summary>
    public ConditionFunnel Conditions { get; } = new("B1B2", "B3", "B4", "B5");

    public override string? FunnelLine() =>
        $"context {_contextEntered:N0} → candidates {_candidates:N0} | {Conditions.Summary()}";

    public StopRunFadeDetector(
        TickSize tick,
        Setup2Options options,
        TradeDirection direction,
        RiskManager risk,
        IExecutionPort exec,
        ICandidateJournal journal,
        ExecutionOptions execOptions,
        RiskOptions riskOptions,
        Func<long> nextCandidateId)
        : base(SetupId.StopRunFade, direction, tick, risk, exec, journal, execOptions, riskOptions, nextCandidateId)
    {
        _o = options;
    }

    /// <summary>True for the stated short (sweeps a high); false for the long mirror (sweeps a low).</summary>
    private bool High => Direction == TradeDirection.Short;

    private Side EntrySide => Direction == TradeDirection.Long ? Side.Bid : Side.Ask;

    private Side ExitSide => Direction == TradeDirection.Long ? Side.Ask : Side.Bid;

    /// <summary>The resting side whose liquidity confirms the trap (offers above for the short).</summary>
    private Side SupplySide => Direction == TradeDirection.Long ? Side.Bid : Side.Ask;

    /// <summary>Ticks <paramref name="p"/> sits beyond <paramref name="reference"/> in the sweep
    /// direction (positive = further into the sweep).</summary>
    private long BeyondTicks(Price p, Price reference) =>
        High ? p.TicksFrom(reference, Tick) : reference.TicksFrom(p, Tick);

    protected override void Step(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        if (!_o.Enabled)
        {
            return;
        }
        switch (State)
        {
            case SetupState.Idle when e.Kind == MarketEventKind.Trade:
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
        _h = Price.Undefined;
        _sweepExtreme = Price.Undefined;
        _sweepFrozen = false;
        _maxBeyondSweepTicks = 0;
        _entryOrderId = -1;
        _stopOrderId = -1;
        _t1OrderId = -1;
        _t2OrderId = -1;
        _exitOrderId = -1;
        _heldBackPastLevel = false;
    }

    // ----- Idle → ContextMet -----

    private void TryEnterContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        // B1 + B2: a reference level exists and price swept it within the zone.
        if (!Conditions.Check(B1B2, features.TryGetSweptReference(
                e.Price, High, _o.SweepZoneMinTicks, _o.SweepZoneMaxTicks, out var h)))
        {
            return;
        }

        var bar = features.FormingBar;
        long barAggressive = High ? bar.BuyVolume : bar.SellVolume;
        long? volumeP90 = features.BarAggressiveVolumeQuantile(buySide: High, _o.ClimaxVolumePercentile);
        long volumeBeyond = FootprintFeatures.AggressorVolumeBeyond(bar, Tick, h, buySide: High, atOrAbove: High);
        if (!Conditions.Check(B3, Setup2Guards.B3_Climax(barAggressive, volumeP90, volumeBeyond, barAggressive, _o)))
        {
            return;
        }

        _h = h;
        Level = h;
        _sweepExtreme = e.Price;
        _sweepTs = e.TsEvent;
        _sweepFrozen = false;
        _maxBeyondSweepTicks = 0;
        _contextEntered++;
        SampleVolatilityRegime(features);
        State = SetupState.ContextMet;
    }

    // ----- ContextMet → Armed -----

    private void StepContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        if (BeyondTicks(e.Price, _h) > _o.SweepZoneMaxTicks                                  // became a breakout
            || (long)(e.TsEvent.UnixNanos - _sweepTs.UnixNanos) >= _o.SweepValiditySeconds * 1_000_000_000L)
        {
            ResetToIdle();
            return;
        }
        if (e.Kind == MarketEventKind.Trade)
        {
            TrackSweep(e.Price);
        }
        if (!Conditions.Check(B4, Setup2Guards.B4_NoFollowThrough(_maxBeyondSweepTicks, _o)))
        {
            ResetToIdle(); // follow-through — a continuation, not a trap
            return;
        }

        int stackedLen = FootprintFeatures.StackedImbalanceLen(
            features.FormingBar, Tick, _o.ImbalanceRatio, sellSide: High);
        if (!Conditions.Check(B5, Setup2Guards.B5_SupplyConfirms(
                features.Liquidity.DepthChangeBeyondBest(SupplySide), stackedLen, _o)))
        {
            return;
        }

        _candidates++;
        long sweepBeyondH = BeyondTicks(_sweepExtreme, _h);
        long stopDistanceTicks = sweepBeyondH + _o.StopAboveSweepTicks + _o.EntryOffsetTicks + ExecOpts.StopSlippageTicks;
        var verdict = EmitCandidate(in e, tracker, features, _h, stopDistanceTicks);
        if (!verdict.Approved)
        {
            ResetToIdle();
            return;
        }
        State = SetupState.Armed;
        Risk.Reserve(Level.RawNano);
        _entryOrderId = Exec.Place(new OrderSpec(
            EntrySide, OrderType.StopMarket, _h.AddTicks(Sign * _o.EntryOffsetTicks, Tick), verdict.Quantity));
        State = SetupState.OrderWorking;
    }

    /// <summary>Tracks the sweep high: it rises while the thrust extends, freezes on the first
    /// pullback, and afterward records any new high beyond it (B4 follow-through).</summary>
    private void TrackSweep(Price price)
    {
        long beyond = BeyondTicks(price, _sweepExtreme);
        if (!_sweepFrozen)
        {
            if (beyond > 0)
            {
                _sweepExtreme = price;
            }
            else if (beyond <= -1)
            {
                _sweepFrozen = true;
            }
            return;
        }
        _maxBeyondSweepTicks = Math.Max(_maxBeyondSweepTicks, beyond);
    }

    // ----- OrderWorking -----

    private void StepOrderWorking(in MarketEvent e)
    {
        if (e.Kind == MarketEventKind.Trade)
        {
            TrackSweep(e.Price);
        }
        if ((long)(e.TsEvent.UnixNanos - _sweepTs.UnixNanos) >= _o.SweepValiditySeconds * 1_000_000_000L
            || !Setup2Guards.B4_NoFollowThrough(_maxBeyondSweepTicks, _o))
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
        if (e.Kind != MarketEventKind.Trade)
        {
            return; // the scratch rule tracks traded price, not book-level activity
        }
        // Scratch: price re-crossed past the level and has held there.
        bool pastLevel = BeyondTicks(e.Price, _h) > 0;
        if (pastLevel)
        {
            if (!_heldBackPastLevel)
            {
                _heldBackPastLevel = true;
                _heldBackSince = e.TsEvent;
            }
            else if ((long)(e.TsEvent.UnixNanos - _heldBackSince.UnixNanos) >= _o.ScratchSeconds * 1_000_000_000L)
            {
                ExitAtMarket(ExitReason.Scratch);
            }
        }
        else
        {
            _heldBackPastLevel = false;
        }
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
        _heldBackPastLevel = false;
        var stopPrice = _sweepExtreme.AddTicks(-Sign * _o.StopAboveSweepTicks, Tick);
        _rTicks = Sign * (fill.Price.RawNano - stopPrice.RawNano) / Tick.RawNano;
        _stopOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.StopMarket, stopPrice, Remaining));
        long t1Ticks = (long)Math.Round(_o.T1RMultiple * _rTicks, MidpointRounding.AwayFromZero);
        long t1Qty = Math.Clamp((long)Math.Floor(fill.Quantity * _o.T1ExitFraction), 1, fill.Quantity);
        _t1OrderId = Exec.Place(new OrderSpec(
            ExitSide, OrderType.Limit, fill.Price.AddTicks(Sign * t1Ticks, Tick), t1Qty));
    }

    /// <summary>T2: developing POC in the profit direction, capped at T2RCap × R (the rulebook's
    /// "origin of the breakout leg" is not computed in v1 — documented).</summary>
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
