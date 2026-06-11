using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

public enum OrderType : byte
{
    /// <summary>Resting limit; fills ONLY when the market trades through the price (v1 fill model).</summary>
    Limit = 1,

    /// <summary>Stop that becomes marketable at the trigger; fills at trigger + adverse slippage.</summary>
    StopMarket = 2,

    /// <summary>Immediate; fills at the current opposing touch + adverse slippage.</summary>
    Market = 3,
}

/// <summary>An order request. Side uses book convention: Bid = buy, Ask = sell.
/// Price is the limit price or stop trigger; ignored (Undefined) for Market.</summary>
public readonly record struct OrderSpec(
    Side Side,
    OrderType Type,
    Price Price,
    long Quantity);

/// <summary>An execution. Ts is the timestamp of the market event that resolved the order
/// (placement time for Market orders) — event time only, never wall clock.</summary>
public readonly record struct Fill(
    long OrderId,
    Timestamp Ts,
    Side Side,
    Price Price,
    long Quantity);

/// <summary>Receives fills synchronously as they are resolved, in deterministic order.</summary>
public interface IFillListener
{
    void OnFill(in Fill fill);
}

/// <summary>
/// Execution layer port (CLAUDE.md: live trading is an interface only in v1 — the fill
/// simulator is the sole adapter). Implementations must be deterministic given the same
/// event stream and call sequence.
/// </summary>
public interface IExecutionPort
{
    /// <summary>Places an order, returning its id. Market orders may fill (via the
    /// listener) before this returns.</summary>
    long Place(in OrderSpec spec);

    /// <summary>Cancels a resting order; false when it no longer exists (filled/cancelled).</summary>
    bool Cancel(long orderId);
}

/// <summary>An execution adapter driven by replayed market events (the fill simulator).
/// The pipeline feeds it each event BEFORE detectors react to that event, so orders
/// placed in reaction to event N resolve no earlier than event N+1. A live broker
/// adapter would not implement this — fills arrive from the venue instead.</summary>
public interface IEventDrivenExecution
{
    void OnMarketEvent(in Events.MarketEvent e);
}

/// <summary>Fill-model and cost knobs (CLAUDE.md fill model; appsettings "Execution").</summary>
public sealed class ExecutionOptions
{
    /// <summary>Adverse slippage in ticks applied to stop-market and market fills.</summary>
    public int StopSlippageTicks { get; set; } = 1;

    /// <summary>All-in round-turn commission per contract (ES retail approximation).</summary>
    public decimal CommissionPerContractRoundTurn { get; set; } = 1.40m;
}
