using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Setup 4 — LVN vacuum continuation. Stated for the downside (short) version; the long
/// mirror flips through <see cref="SetupDetectorBase.Sign"/>. "Continuation" is the trade
/// direction (down for the short). Transitions (each rulebook condition is a guard in
/// <see cref="LvnVacuumGuards"/>):
///
///   Idle → ContextMet      D1: last price within LvnProximityTicks of a profile LVN in the
///                          trade direction, with the next HVN ≥ HvnRoomTicks beyond it.
///   ContextMet → Armed     D2 (pulling: depth decline + cancels dominating) + D3 (aligned
///                          aggressor delta), then global filters 1–6; blocked candidates
///                          are journaled and the machine re-checks context from Idle.
///   Armed → OrderWorking   sell stop-market 1 tick beyond the LVN; its trigger realizes D4
///                          (best ticks through the LVN). Cancelled if unfilled past
///                          EntryExpirySeconds or if price leaves the LVN's proximity.
///   InPosition             stop 1 tick beyond the LVN zone against the trade; single target
///                          front-running the next HVN by 1 tick (no runners); time stop
///                          TimeStopSeconds; flat at RTH end.
///
/// MBP-10 approximations (documented per CLAUDE.md): D2's "5 levels below last price"
/// depth decline is read from F21 (<see cref="LiquidityDynamicsTracker.DepthChangeBeyondBest"/>,
/// the levels beyond the best); the LVN "zone" width is the configurable stop offset
/// (we treat the LVN as a single price). D4 is enforced by the entry stop trigger, not a
/// separate guard.
/// </summary>
public sealed class LvnVacuumDetector : SetupDetectorBase
{
    private readonly LvnVacuumOptions _o;
    private int _deltaWindowIdx = -1;
    private Price _lastTradePrice = Price.Undefined;

    private Price _hvn = Price.Undefined;
    private long _entryOrderId = -1;
    private long _stopOrderId = -1;
    private long _targetOrderId = -1;
    private long _exitOrderId = -1;
    private Timestamp _armTs;
    private Timestamp _entryTs;

    private long _contextEntered;
    private long _candidates;

    private const int D1Lvn = 0, D1Room = 1, D2 = 2, D3 = 3;

    /// <summary>Per-condition funnel telemetry (diagnostic only). D1 is split into its two
    /// halves — "an LVN (with a next HVN) is within proximity" and "room to pay" — because
    /// the first can silence the setup without the rulebook threshold ever being tested.
    /// Evaluated counts are per event (this detector re-checks context continuously).</summary>
    public ConditionFunnel Conditions { get; } = new("D1lvn", "D1room", "D2", "D3");

    public override string? FunnelLine() =>
        $"context {_contextEntered:N0} → candidates {_candidates:N0} | {Conditions.Summary()}";

    public LvnVacuumDetector(
        TickSize tick,
        LvnVacuumOptions options,
        TradeDirection direction,
        RiskManager risk,
        IExecutionPort exec,
        ICandidateJournal journal,
        ExecutionOptions execOptions,
        RiskOptions riskOptions,
        Func<long> nextCandidateId)
        : base(SetupId.LvnVacuum, direction, tick, risk, exec, journal, execOptions, riskOptions, nextCandidateId)
    {
        _o = options;
    }

    /// <summary>Continuation is up for the long mirror, down for the stated short.</summary>
    private bool ContinuationUp => Direction == TradeDirection.Long;

    /// <summary>The side whose displayed liquidity is being pulled ahead of price (bids for
    /// the downside short, asks for the upside long).</summary>
    private Side PullSide => Direction == TradeDirection.Long ? Side.Ask : Side.Bid;

    private Side EntrySide => ExecBuySide;

    private Side ExitSide => ExecSellSide;

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
        _hvn = Price.Undefined;
        _entryOrderId = -1;
        _stopOrderId = -1;
        _targetOrderId = -1;
        _exitOrderId = -1;
    }

    // ----- Idle / ContextMet -----

    private void StepContext(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        // D1: an LVN within proximity in the trade direction, with room to the next HVN.
        Price lvn = Price.Undefined;
        Price? hvnFound = null;
        bool lvnNear = !_lastTradePrice.IsUndefined
            && features.Profile.TryGetNearestLvn(_lastTradePrice, ContinuationUp, _o.LvnProximityTicks, out lvn)
            && (hvnFound = features.Profile.NextHvn(lvn, ContinuationUp)) is not null;
        if (!Conditions.Check(D1Lvn, lvnNear))
        {
            if (State == SetupState.ContextMet)
            {
                ResetToIdle();
            }
            return;
        }
        var hvn = hvnFound!.Value;
        long lvnToHvnTicks = Math.Abs(hvn.TicksFrom(lvn, Tick));
        if (!Conditions.Check(D1Room, LvnVacuumGuards.D1_RoomToPay(lvnToHvnTicks, _o)))
        {
            if (State == SetupState.ContextMet)
            {
                ResetToIdle();
            }
            return;
        }
        if (State != SetupState.ContextMet)
        {
            _contextEntered++;
            SampleVolatilityRegime(features);
        }
        Level = lvn;
        _hvn = hvn;
        State = SetupState.ContextMet;

        // D2 + D3: the book ahead is pulling and aggressors are aligned with the continuation.
        if (_deltaWindowIdx < 0)
        {
            _deltaWindowIdx = features.FlowWindowIndex(_o.DeltaWindowSeconds);
        }
        if (!Conditions.Check(D2, LvnVacuumGuards.D2_Pulling(
                features.Liquidity.DepthChangeBeyondBest(PullSide),
                features.Liquidity.PullRatio(PullSide, _deltaWindowIdx), _o)))
        {
            return;
        }
        long directionalDelta = Sign * features.WindowDelta(_deltaWindowIdx);
        if (!Conditions.Check(D3, LvnVacuumGuards.D3_AggressorAlignment(directionalDelta, _o)))
        {
            return;
        }

        // Candidate: global filters + journal (blocked candidates re-check context from Idle).
        _candidates++;
        long stopDistanceTicks = _o.EntryOffsetTicks + ExecOpts.StopSlippageTicks + _o.StopOffsetTicks;
        var verdict = EmitCandidate(in e, tracker, features, _lastTradePrice, stopDistanceTicks);
        if (!verdict.Approved)
        {
            ResetToIdle();
            return;
        }
        State = SetupState.Armed;
        Risk.Reserve(Level.RawNano);
        _entryOrderId = Exec.Place(new OrderSpec(
            EntrySide, OrderType.StopMarket, Level.AddTicks(ExecSign * _o.EntryOffsetTicks, Tick), verdict.Quantity));
        _armTs = e.TsEvent;
        State = SetupState.OrderWorking;
    }

    // ----- OrderWorking -----

    private void StepOrderWorking(in MarketEvent e)
    {
        long sinceArm = (long)(e.TsEvent.UnixNanos - _armTs.UnixNanos);
        if (sinceArm >= _o.EntryExpirySeconds * 1_000_000_000L)
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
        long sinceEntry = (long)(e.TsEvent.UnixNanos - _entryTs.UnixNanos);
        if (sinceEntry >= _o.TimeStopSeconds * 1_000_000_000L)
        {
            ExitAtMarket(ExitReason.TimeStop);
        }
    }

    private void ExitAtMarket(ExitReason reason)
    {
        CancelOrder(ref _stopOrderId);
        CancelOrder(ref _targetOrderId);
        PendingExitReason = reason;
        _exitOrderId = Exec.Place(new OrderSpec(ExitSide, OrderType.Market, Price.Undefined, Remaining));
    }

    // ----- fills -----

    public override void OnFill(in Fill fill, FeatureEngine features)
    {
        if (fill.OrderId == _entryOrderId)
        {
            _entryOrderId = -1;
            OpenPosition(in fill);
            _entryTs = fill.Ts;
            // Stop 1 tick beyond the LVN zone against the trade; single target front-running
            // the next HVN by 1 tick (100% exit — vacuum trades get no runners).
            _stopOrderId = Exec.Place(new OrderSpec(
                ExitSide, OrderType.StopMarket, Level.AddTicks(-ExecSign * _o.StopOffsetTicks, Tick), Remaining));
            _targetOrderId = Exec.Place(new OrderSpec(
                ExitSide, OrderType.Limit, _hvn.AddTicks(-ExecSign * _o.TargetFrontRunTicks, Tick), Remaining));
            return;
        }

        ExitFills.Add(fill);
        Remaining -= fill.Quantity;

        if (fill.OrderId == _stopOrderId)
        {
            _stopOrderId = -1;
            CancelOrder(ref _targetOrderId);
            FinalizeTrade(ExitReason.Stop);
        }
        else if (fill.OrderId == _targetOrderId)
        {
            _targetOrderId = -1;
            CancelOrder(ref _stopOrderId);
            FinalizeTrade(ExitReason.Target);
        }
        else if (fill.OrderId == _exitOrderId && Remaining <= 0)
        {
            _exitOrderId = -1;
            FinalizeTrade(PendingExitReason);
        }
    }
}
