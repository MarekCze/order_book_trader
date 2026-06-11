using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest;

/// <summary>
/// Conservative fill simulator (CLAUDE.md fill model, deliberately biased against us):
///  - Limit orders fill ONLY when a trade prints strictly through the limit price
///    (touch is never enough — no queue modeling in v1). Fill price = limit price.
///  - Stop-market orders trigger on a trade at or through the trigger and fill at
///    trigger + <see cref="ExecutionOptions.StopSlippageTicks"/> adverse.
///  - Market orders fill immediately at the current opposing touch + the same adverse
///    slippage (CLAUDE.md only specifies stop-market; "exit at market" reuses its model).
/// Orders are evaluated against each event in placement (id) order. An order placed while
/// event N is being processed (e.g. by a detector reacting to a fill from event N) is
/// first eligible on event N+1 — enforced via a per-event epoch tag. Fills for one event
/// are collected first and emitted after the resolution pass, so listeners may place or
/// cancel orders reentrantly. Fills are all-or-nothing.
/// </summary>
public sealed class FillSimulator : IExecutionPort, IEventDrivenExecution
{
    private readonly TickSize _tick;
    private readonly ExecutionOptions _opts;
    private readonly List<(long Id, OrderSpec Spec, long Epoch)> _resting = new();
    private readonly List<Fill> _emitting = new();
    private long _nextId;
    private long _epoch;
    private Timestamp _now;
    private Price _bestBid = Price.Undefined;
    private Price _bestAsk = Price.Undefined;

    public FillSimulator(TickSize tick, ExecutionOptions options, IFillListener? listener = null)
    {
        _tick = tick;
        _opts = options;
        Listener = listener;
    }

    /// <summary>Fill receiver — settable after construction because the coordinator that
    /// listens also needs this simulator as its execution port.</summary>
    public IFillListener? Listener { get; set; }

    /// <summary>Resting (unfilled, uncancelled) order count — used by tests and sanity checks.</summary>
    public int RestingCount => _resting.Count;

    public void OnMarketEvent(in MarketEvent e)
    {
        _epoch++;
        _now = e.TsEvent;
        if (e.Kind is MarketEventKind.BookChanged or MarketEventKind.Trade)
        {
            var top = e.Levels[0];
            _bestBid = top.HasBid ? top.BidPrice : Price.Undefined;
            _bestAsk = top.HasAsk ? top.AskPrice : Price.Undefined;
        }
        if (e.Kind != MarketEventKind.Trade)
        {
            return;
        }

        // Resolve in placement order; collect, then emit, so listener reactions
        // (cancel/place) cannot disturb the resolution pass.
        _emitting.Clear();
        for (int i = 0; i < _resting.Count;)
        {
            var (id, spec, epoch) = _resting[i];
            if (epoch < _epoch && TryResolve(in spec, e.Price) is { } fillPrice)
            {
                _resting.RemoveAt(i);
                _emitting.Add(new Fill(id, _now, spec.Side, fillPrice, spec.Quantity));
            }
            else
            {
                i++;
            }
        }
        for (int i = 0; i < _emitting.Count; i++)
        {
            var fill = _emitting[i];
            Listener?.OnFill(in fill);
        }
    }

    public long Place(in OrderSpec spec)
    {
        long id = ++_nextId;
        if (spec.Type == OrderType.Market)
        {
            var touch = spec.Side == Side.Bid ? _bestAsk : _bestBid;
            long adverse = spec.Side == Side.Bid ? _opts.StopSlippageTicks : -_opts.StopSlippageTicks;
            if (touch.IsUndefined)
            {
                // No book state (e.g. just cleared): rest and fill on the next trade at
                // its print price + adverse slippage (see TryResolve).
                _resting.Add((id, spec, _epoch));
                return id;
            }
            var fill = new Fill(id, _now, spec.Side, touch.AddTicks(adverse, _tick), spec.Quantity);
            Listener?.OnFill(in fill);
            return id;
        }
        _resting.Add((id, spec, _epoch));
        return id;
    }

    public bool Cancel(long orderId)
    {
        for (int i = 0; i < _resting.Count; i++)
        {
            if (_resting[i].Id == orderId)
            {
                _resting.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Fill price when a trade at <paramref name="tradePrice"/> resolves the order, else null.</summary>
    private Price? TryResolve(in OrderSpec spec, Price tradePrice)
    {
        switch (spec.Type)
        {
            case OrderType.Limit when spec.Side == Side.Bid && tradePrice < spec.Price:
            case OrderType.Limit when spec.Side == Side.Ask && tradePrice > spec.Price:
                return spec.Price;
            case OrderType.StopMarket when spec.Side == Side.Bid && tradePrice >= spec.Price:
                return spec.Price.AddTicks(_opts.StopSlippageTicks, _tick);
            case OrderType.StopMarket when spec.Side == Side.Ask && tradePrice <= spec.Price:
                return spec.Price.AddTicks(-_opts.StopSlippageTicks, _tick);
            case OrderType.Market: // only rests when placed with no book state
                return tradePrice.AddTicks(
                    spec.Side == Side.Bid ? _opts.StopSlippageTicks : -_opts.StopSlippageTicks, _tick);
            default:
                return null;
        }
    }
}
