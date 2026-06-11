using OrderFlow.Backtest;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// Conservative fill model (CLAUDE.md): stop-market fills at trigger + 1 tick adverse
/// slippage; limit orders fill ONLY on trade-through (never on touch); market orders
/// cross the current touch with the same adverse slippage. Orders placed while an event
/// is being processed only become live for SUBSEQUENT events.
/// </summary>
public class FillSimulatorTests
{
    private sealed class Fills : IFillListener
    {
        public readonly List<Fill> List = new();
        public void OnFill(in Fill fill) => List.Add(fill);
    }

    private static readonly TickSize Tick = TickSize.Es;

    private static (FillSimulator Sim, Fills Fills) NewSim()
    {
        var fills = new Fills();
        return (new FillSimulator(Tick, new ExecutionOptions(), fills), fills);
    }

    private static void Trade(FillSimulator sim, decimal px, uint size = 5, ulong ts = 2_000)
    {
        var levels = TestEvents.Levels(
            new (decimal?, uint, uint)[] { (px - 0.25m, 50, 10) },
            new (decimal?, uint, uint)[] { (px + 0.25m, 50, 10) });
        sim.OnMarketEvent(TestEvents.Trade(Side.Ask, px, size, levels, ts));
    }

    private static void Book(FillSimulator sim, decimal bid, decimal ask, ulong ts = 1_500)
    {
        var levels = TestEvents.Levels(
            new (decimal?, uint, uint)[] { (bid, 50, 10) },
            new (decimal?, uint, uint)[] { (ask, 50, 10) });
        sim.OnMarketEvent(TestEvents.BookChanged(
            OrderFlow.Domain.Events.BookCause.Add, Side.Bid, bid, 50, 0, levels, ts: ts));
    }

    // ----- limit orders: trade-through only -----

    [Fact]
    public void BuyLimit_DoesNotFill_OnTouch()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 2));
        Trade(sim, 5000.25m); // trade AT the limit price = touch, not through
        Assert.Empty(fills.List);
    }

    [Fact]
    public void BuyLimit_Fills_OnTradeThrough_AtLimitPrice()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        long id = sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 2));
        Trade(sim, 5000.00m); // strictly below the limit = traded through
        var fill = Assert.Single(fills.List);
        Assert.Equal(id, fill.OrderId);
        Assert.Equal(Price.FromDecimal(5000.25m), fill.Price);
        Assert.Equal(2, fill.Quantity);
        Assert.Equal(Side.Bid, fill.Side);
    }

    [Fact]
    public void SellLimit_Fills_OnlyOnTradeAbove()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        sim.Place(new OrderSpec(Side.Ask, OrderType.Limit, Price.FromDecimal(5000.50m), 3));
        Trade(sim, 5000.50m); // touch
        Assert.Empty(fills.List);
        Trade(sim, 5000.75m); // through
        var fill = Assert.Single(fills.List);
        Assert.Equal(Price.FromDecimal(5000.50m), fill.Price);
    }

    [Fact]
    public void Limit_FillsOnce_NotAgainOnLaterTrades()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 1));
        Trade(sim, 5000.00m);
        Trade(sim, 4999.75m);
        Assert.Single(fills.List);
    }

    // ----- stop-market orders: trigger + adverse slippage -----

    [Fact]
    public void BuyStop_Fills_AtTriggerPlusOneTick_WhenTradeAtOrAboveTrigger()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.25m);
        sim.Place(new OrderSpec(Side.Bid, OrderType.StopMarket, Price.FromDecimal(5001.00m), 2));
        Trade(sim, 5000.75m); // below trigger: nothing
        Assert.Empty(fills.List);
        Trade(sim, 5001.00m); // at trigger
        var fill = Assert.Single(fills.List);
        Assert.Equal(Price.FromDecimal(5001.25m), fill.Price); // +1 tick adverse
    }

    [Fact]
    public void SellStop_Fills_AtTriggerMinusOneTick()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.25m);
        sim.Place(new OrderSpec(Side.Ask, OrderType.StopMarket, Price.FromDecimal(4999.00m), 4));
        Trade(sim, 4998.75m); // below trigger also triggers
        var fill = Assert.Single(fills.List);
        Assert.Equal(Price.FromDecimal(4998.75m), fill.Price); // 4999.00 − 1 tick
        Assert.Equal(4, fill.Quantity);
    }

    // ----- market orders: immediate at the touch + slippage -----

    [Fact]
    public void MarketSell_FillsImmediately_AtBestBidMinusSlippage()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.25m);
        sim.Place(new OrderSpec(Side.Ask, OrderType.Market, Price.Undefined, 3));
        var fill = Assert.Single(fills.List);
        Assert.Equal(Price.FromDecimal(4999.75m), fill.Price);
    }

    [Fact]
    public void MarketBuy_FillsImmediately_AtBestAskPlusSlippage()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.25m);
        sim.Place(new OrderSpec(Side.Bid, OrderType.Market, Price.Undefined, 1));
        var fill = Assert.Single(fills.List);
        Assert.Equal(Price.FromDecimal(5000.50m), fill.Price);
    }

    // ----- lifecycle -----

    [Fact]
    public void Cancel_PreventsLaterFill()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        long id = sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 1));
        Assert.True(sim.Cancel(id));
        Trade(sim, 5000.00m);
        Assert.Empty(fills.List);
        Assert.False(sim.Cancel(id)); // already gone
    }

    [Fact]
    public void OrderPlacedDuringEvent_DoesNotFillOnThatSameEvent()
    {
        // The pipeline resolves fills for event N BEFORE detectors react to event N; an
        // order placed in reaction to event N must not be filled by event N itself.
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        Trade(sim, 5000.00m); // event N
        sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 1));
        Assert.Empty(fills.List);   // not filled retroactively by event N
        Trade(sim, 5000.00m); // event N+1
        Assert.Single(fills.List);
    }

    [Fact]
    public void MultipleOrders_FillInPlacementOrder()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        long first = sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 1));
        long second = sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.50m), 1));
        Trade(sim, 5000.00m);
        Assert.Equal(2, fills.List.Count);
        Assert.Equal(first, fills.List[0].OrderId);
        Assert.Equal(second, fills.List[1].OrderId);
    }

    [Fact]
    public void FillTimestamp_IsTheTriggeringEventTimestamp()
    {
        var (sim, fills) = NewSim();
        Book(sim, 5000.00m, 5000.50m);
        sim.Place(new OrderSpec(Side.Bid, OrderType.Limit, Price.FromDecimal(5000.25m), 1));
        Trade(sim, 5000.00m, ts: 7_777);
        Assert.Equal(new Timestamp(7_777), fills.List[0].Ts);
    }
}
