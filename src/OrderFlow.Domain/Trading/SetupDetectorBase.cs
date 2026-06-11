using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Domain.Trading;

/// <summary>
/// Common base for setup detectors (CLAUDE.md: one class per setup, sharing a common
/// base). Owns the lifecycle plumbing every setup shares: event gating (maintenance
/// break, RTH-only operation, Closed → Idle reset), candidate emission through the
/// global risk filters with the full feature snapshot journaled, position excursion
/// tracking (MAE/MFE), market exits, and trade finalization (P&L net of commission,
/// journal outcome, exposure release). Setup-specific guards, orders and transitions
/// live in the derived class. Everything runs on event time — no wall clock.
/// </summary>
public abstract class SetupDetectorBase
{
    protected readonly TickSize Tick;
    protected readonly RiskManager Risk;
    protected readonly IExecutionPort Exec;
    protected readonly ICandidateJournal Journal;
    protected readonly ExecutionOptions ExecOpts;
    protected readonly RiskOptions RiskOpts;
    private readonly Func<long> _nextCandidateId;

    protected SetupDetectorBase(
        SetupId setup,
        TradeDirection direction,
        TickSize tick,
        RiskManager risk,
        IExecutionPort exec,
        ICandidateJournal journal,
        ExecutionOptions execOptions,
        RiskOptions riskOptions,
        Func<long> nextCandidateId)
    {
        Setup = setup;
        Direction = direction;
        Tick = tick;
        Risk = risk;
        Exec = exec;
        Journal = journal;
        ExecOpts = execOptions;
        RiskOpts = riskOptions;
        _nextCandidateId = nextCandidateId;
    }

    public SetupId Setup { get; }

    public TradeDirection Direction { get; }

    public SetupState State { get; protected set; }

    /// <summary>+1 for long, −1 for short: guards and price offsets are written in long
    /// space and mirrored through this sign.</summary>
    protected int Sign => Direction == TradeDirection.Long ? 1 : -1;

    // ----- active candidate / trade state -----

    protected long CandidateId { get; private set; }

    /// <summary>The defended level L — also the per-LOI key for risk filter 5.</summary>
    protected Price Level { get; set; }

    protected Fill EntryFill { get; private set; }

    protected bool T1Filled { get; set; }

    /// <summary>Contracts still open (entry fill minus exit fills).</summary>
    protected long Remaining { get; set; }

    protected long MaeTicks { get; private set; }

    protected long MfeTicks { get; private set; }

    protected List<Fill> ExitFills { get; } = new();

    /// <summary>Exit reason carried by a pending market exit until its fill arrives.</summary>
    protected ExitReason PendingExitReason { get; set; }

    public void OnEvent(in MarketEvent e, BookStateTracker tracker, FeatureEngine features)
    {
        if (e.Kind is not (MarketEventKind.BookChanged or MarketEventKind.Trade))
        {
            return;
        }
        if (CmeSessions.IsMaintenanceBreak(e.TsEvent))
        {
            return; // crossed pre-open books are market reality, not signals
        }
        if (State == SetupState.Closed)
        {
            ResetToIdle();
        }
        if (!CmeSessions.IsRth(e.TsEvent))
        {
            OnOutsideRth(in e, tracker, features);
            return;
        }
        Step(in e, tracker, features);
    }

    /// <summary>Per-event state machine logic during RTH.</summary>
    protected abstract void Step(in MarketEvent e, BookStateTracker tracker, FeatureEngine features);

    /// <summary>Called for events outside RTH (detectors trade RTH only — global filter 1).
    /// Derived classes flatten positions / cancel entries; context states just reset.</summary>
    protected abstract void OnOutsideRth(in MarketEvent e, BookStateTracker tracker, FeatureEngine features);

    /// <summary>Routes a fill from the execution layer; features are passed through so exit
    /// targets can read current structure (e.g. the developing POC).</summary>
    public abstract void OnFill(in Fill fill, FeatureEngine features);

    /// <summary>Clear setup-specific accumulators on a reset to Idle.</summary>
    protected abstract void OnReset();

    /// <summary>
    /// Runs the candidate through the global filters and journals it with the full
    /// feature snapshot — every candidate is journaled, blocked or not.
    /// </summary>
    protected RiskVerdict EmitCandidate(
        in MarketEvent e,
        BookStateTracker tracker,
        FeatureEngine features,
        Price lastTradePrice,
        long stopDistanceTicks)
    {
        long? loiDistance = !lastTradePrice.IsUndefined && features.TryGetNearestLoi(lastTradePrice, out var loi)
            ? loi.SignedDistanceTicks
            : null;
        long? spread = tracker.TryGetSpreadTicks(Tick, out var s) ? s : null;
        var ctx = new CandidateContext(
            Ts: e.TsEvent,
            LevelKey: Level.RawNano,
            LoiDistanceTicks: loiDistance,
            AtrPercentile: features.AtrPercentile,
            NewsWindow: features.IsNewsWindow(e.TsEvent),
            SpreadTicks: spread,
            StopDistanceTicks: stopDistanceTicks);
        var verdict = Risk.Evaluate(in ctx);
        CandidateId = _nextCandidateId();
        Journal.RecordCandidate(new CandidateRecord
        {
            Id = CandidateId,
            Ts = e.TsEvent,
            SessionDate = CmeSessions.SessionDate(e.TsEvent),
            Setup = Setup,
            Direction = Direction,
            Level = Level,
            Block = verdict.Block,
            Quantity = verdict.Quantity,
            Features = features.ComputeSnapshot(tracker),
        });
        return verdict;
    }

    /// <summary>The entry order fill: transition to InPosition and start excursion tracking.</summary>
    protected void OpenPosition(in Fill fill)
    {
        EntryFill = fill;
        Remaining = fill.Quantity;
        T1Filled = false;
        MaeTicks = 0;
        MfeTicks = 0;
        ExitFills.Clear();
        PendingExitReason = ExitReason.None;
        State = SetupState.InPosition;
    }

    /// <summary>MAE/MFE in ticks from the entry fill, driven by trade prints.</summary>
    protected void UpdateExcursions(Price tradePrice)
    {
        long excursion = Sign * (tradePrice.RawNano - EntryFill.Price.RawNano) / Tick.RawNano;
        MfeTicks = Math.Max(MfeTicks, excursion);
        MaeTicks = Math.Max(MaeTicks, -excursion);
    }

    /// <summary>Closes out a candidate whose entry never executed (expiry, pre-fill
    /// invalidation, session end while working).</summary>
    protected void FinalizeNoTrade(CandidateDisposition disposition, ExitReason reason)
    {
        Journal.RecordOutcome(new CandidateOutcome
        {
            CandidateId = CandidateId,
            Disposition = disposition,
            ExitReason = reason,
            EntryFill = null,
            ExitFills = Array.Empty<Fill>(),
            T1Filled = false,
            MaeTicks = 0,
            MfeTicks = 0,
            GrossPnl = 0m,
            Commission = 0m,
            NetPnl = 0m,
        });
        Risk.Release(Level.RawNano, TradeResult.NoFill);
        State = SetupState.Closed;
    }

    /// <summary>Position fully closed: P&L net of commission, journal, release exposure.
    /// Filter 5 stop-out accounting: a Stop or Invalidation exit before T1 is a stop-out
    /// at the level (interpretation: an invalidation IS the level failing); after T1 the
    /// level held, so the result is Win/Other by net P&L.</summary>
    protected void FinalizeTrade(ExitReason reason)
    {
        decimal gross = 0m;
        foreach (var fill in ExitFills)
        {
            long ticks = Sign * (fill.Price.RawNano - EntryFill.Price.RawNano) / Tick.RawNano;
            gross += ticks * RiskOpts.TickValue * fill.Quantity;
        }
        decimal commission = ExecOpts.CommissionPerContractRoundTurn * EntryFill.Quantity;
        decimal net = gross - commission;
        var result = !T1Filled && reason is ExitReason.Stop or ExitReason.Invalidation
            ? TradeResult.StopOut
            : net > 0 ? TradeResult.Win : TradeResult.Other;
        Journal.RecordOutcome(new CandidateOutcome
        {
            CandidateId = CandidateId,
            Disposition = CandidateDisposition.Traded,
            ExitReason = reason,
            EntryFill = EntryFill,
            ExitFills = ExitFills.ToArray(),
            T1Filled = T1Filled,
            MaeTicks = MaeTicks,
            MfeTicks = MfeTicks,
            GrossPnl = gross,
            Commission = commission,
            NetPnl = net,
        });
        Risk.Release(Level.RawNano, result);
        State = SetupState.Closed;
    }

    protected void ResetToIdle()
    {
        T1Filled = false;
        Remaining = 0;
        MaeTicks = 0;
        MfeTicks = 0;
        ExitFills.Clear();
        PendingExitReason = ExitReason.None;
        OnReset();
        State = SetupState.Idle;
    }

    /// <summary>Cancels an order id if one is held; resets the slot to −1.</summary>
    protected void CancelOrder(ref long orderId)
    {
        if (orderId >= 0)
        {
            Exec.Cancel(orderId);
            orderId = -1;
        }
    }
}
