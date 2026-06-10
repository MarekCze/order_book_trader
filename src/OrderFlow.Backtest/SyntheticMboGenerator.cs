using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Backtest;

/// <summary>
/// Deterministic (seeded) synthetic MBO stream: a random-walk limit order book with adds,
/// cancels, modifies and aggressor trades (fills + trade summary), guaranteed never to
/// produce a crossed book. Used for benchmarks, the synth CLI command, and property tests.
/// Test/benchmark infrastructure only — the production pipeline forbids randomness; this
/// seeded generator is itself fully deterministic for a given (seed, count).
/// </summary>
public sealed class SyntheticMboGenerator
{
    private static readonly Side[] SeedSides = { Side.Bid, Side.Ask };

    private readonly Random _rng;
    private readonly TickSize _tick = TickSize.Es;
    private readonly uint _instrumentId;
    private readonly long _anchorPrice;
    private readonly bool _seedFlaggedAsSnapshot;

    private readonly OrderBook _book = new(); // internal mirror so generated flow stays consistent
    private readonly List<ulong> _liveIds = new();
    private readonly Dictionary<ulong, int> _liveIndex = new();
    private readonly Dictionary<ulong, (Side Side, long PriceRaw, uint Size)> _orders = new();
    private readonly Dictionary<long, List<ulong>> _bidQueues = new();
    private readonly Dictionary<long, List<ulong>> _askQueues = new();

    private ulong _nextOrderId = 1;
    private uint _sequence;
    private ulong _ts;

    public SyntheticMboGenerator(
        int seed = 42,
        uint instrumentId = 1,
        decimal startPrice = 5000.00m,
        ulong startTsNs = 1_767_621_600_000_000_000UL, // 2026-01-05 14:00:00 UTC
        bool seedFlaggedAsSnapshot = false)
    {
        _rng = new Random(seed);
        _instrumentId = instrumentId;
        _anchorPrice = Price.FromDecimal(startPrice).RawNano;
        _ts = startTsNs;
        _seedFlaggedAsSnapshot = seedFlaggedAsSnapshot;
    }

    public IEnumerable<MarketEvent> Generate(long count)
    {
        long produced = 0;

        // Seed a resting book: 30 levels per side, 2 orders per level.
        var seedFlags = _seedFlaggedAsSnapshot ? MarketEventFlags.Snapshot : MarketEventFlags.None;
        for (int lvl = 0; lvl < 30 && produced < count; lvl++)
        {
            foreach (var side in SeedSides)
            {
                if (produced >= count)
                {
                    break;
                }
                long price = side == Side.Bid
                    ? _anchorPrice - (lvl + 1) * _tick.RawNano
                    : _anchorPrice + (lvl + 1) * _tick.RawNano;
                for (int i = 0; i < 2 && produced < count; i++)
                {
                    yield return Emit(MakeAdd(side, price, (uint)(1 + _rng.Next(50)), seedFlags));
                    produced++;
                }
            }
        }

        while (produced < count)
        {
            int roll = _liveIds.Count == 0 ? 0 : _rng.Next(100);
            if (roll < 40)
            {
                yield return Emit(MakeRandomAdd());
                produced++;
            }
            else if (roll < 65)
            {
                yield return Emit(MakeRandomCancel());
                produced++;
            }
            else if (roll < 85)
            {
                yield return Emit(MakeRandomModify());
                produced++;
            }
            else
            {
                var burst = MakeTradeBurst();
                if (burst is null)
                {
                    yield return Emit(MakeRandomAdd());
                    produced++;
                    continue;
                }
                foreach (var e in burst)
                {
                    if (produced >= count)
                    {
                        break;
                    }
                    yield return Emit(e);
                    produced++;
                }
            }
        }
    }

    private MarketEvent Emit(in MarketEvent e)
    {
        Apply(e);
        return e;
    }

    /// <summary>
    /// Builds an aggressor burst — one Fill per resting order touched (FIFO from the best
    /// level, sweeping at most 3 levels), then a single Trade summary at the last fill price.
    /// Events are simulated against local copies and only applied as they are emitted.
    /// </summary>
    private List<MarketEvent>? MakeTradeBurst()
    {
        Side aggressor = _rng.Next(2) == 0 ? Side.Bid : Side.Ask;
        Side resting = aggressor.Opposite();
        Span<PriceLevel> levels = stackalloc PriceLevel[3];
        int levelCount = _book.CopyTopLevels(resting, levels);
        if (levelCount == 0)
        {
            return null;
        }

        uint remaining = (uint)(1 + _rng.Next(120));
        uint traded = 0;
        long lastPrice = 0;
        var events = new List<MarketEvent>(8);
        for (int li = 0; li < levelCount && remaining > 0; li++)
        {
            var queue = Queues(resting)[levels[li].Price.RawNano];
            foreach (ulong id in queue)
            {
                if (remaining == 0)
                {
                    break;
                }
                var order = _orders[id];
                uint qty = Math.Min(remaining, order.Size);
                events.Add(MakeFill(id, order.PriceRaw, qty, resting));
                traded += qty;
                remaining -= qty;
                lastPrice = order.PriceRaw;
            }
        }
        if (traded == 0)
        {
            return null;
        }
        events.Add(MakeTrade(lastPrice, traded, aggressor));
        return events;
    }

    private MarketEvent MakeRandomAdd()
    {
        Side side = _rng.Next(2) == 0 ? Side.Bid : Side.Ask;
        bool hasBid = _book.TryGetBest(Side.Bid, out var bb);
        bool hasAsk = _book.TryGetBest(Side.Ask, out var ba);
        long price;
        if (side == Side.Bid)
        {
            long max = hasAsk ? ba.Price.RawNano - _tick.RawNano : _anchorPrice;
            price = max - _rng.Next(0, 12) * _tick.RawNano;
        }
        else
        {
            long min = hasBid ? bb.Price.RawNano + _tick.RawNano : _anchorPrice + _tick.RawNano;
            price = min + _rng.Next(0, 12) * _tick.RawNano;
        }
        return MakeAdd(side, price, (uint)(1 + _rng.Next(50)), MarketEventFlags.None);
    }

    private MarketEvent MakeRandomCancel()
    {
        ulong id = _liveIds[_rng.Next(_liveIds.Count)];
        var order = _orders[id];
        return Next(MarketEventKind.CancelOrder, id, order.PriceRaw, order.Size, order.Side, MarketEventFlags.None);
    }

    private MarketEvent MakeRandomModify()
    {
        ulong id = _liveIds[_rng.Next(_liveIds.Count)];
        var order = _orders[id];
        if (_rng.Next(100) >= 70)
        {
            long shift = (_rng.Next(2) == 0 ? -1 : 1) * (1 + _rng.Next(2)) * _tick.RawNano;
            long newPrice = order.PriceRaw + shift;
            bool hasBid = _book.TryGetBest(Side.Bid, out var bb);
            bool hasAsk = _book.TryGetBest(Side.Ask, out var ba);
            bool crosses = order.Side == Side.Bid
                ? hasAsk && newPrice >= ba.Price.RawNano
                : hasBid && newPrice <= bb.Price.RawNano;
            if (!crosses)
            {
                return Next(MarketEventKind.ModifyOrder, id, newPrice, order.Size, order.Side, MarketEventFlags.None);
            }
        }
        return Next(MarketEventKind.ModifyOrder, id, order.PriceRaw, (uint)(1 + _rng.Next(50)), order.Side, MarketEventFlags.None);
    }

    private MarketEvent MakeAdd(Side side, long priceRaw, uint size, MarketEventFlags flags) =>
        Next(MarketEventKind.AddOrder, _nextOrderId++, priceRaw, size, side, flags);

    private MarketEvent MakeFill(ulong id, long priceRaw, uint qty, Side restingSide) =>
        Next(MarketEventKind.Fill, id, priceRaw, qty, restingSide, MarketEventFlags.None);

    private MarketEvent MakeTrade(long priceRaw, uint size, Side aggressor) =>
        Next(MarketEventKind.Trade, 0, priceRaw, size, aggressor, MarketEventFlags.None);

    private MarketEvent Next(MarketEventKind kind, ulong orderId, long priceRaw, uint size, Side side, MarketEventFlags flags)
    {
        _ts += (ulong)(1 + _rng.Next(1000)) * 1_000; // 1µs–1ms between events
        _sequence++;
        return new MarketEvent(kind, _instrumentId, new Timestamp(_ts), new Timestamp(_ts + 500_000), _sequence,
            orderId, new Price(priceRaw), size, side, flags);
    }

    private void Apply(in MarketEvent e)
    {
        _book.Apply(in e);
        switch (e.Kind)
        {
            case MarketEventKind.AddOrder:
                _orders[e.OrderId] = (e.Side, e.Price.RawNano, e.Size);
                _liveIndex[e.OrderId] = _liveIds.Count;
                _liveIds.Add(e.OrderId);
                EnqueueAtPrice(e.Side, e.Price.RawNano, e.OrderId);
                break;
            case MarketEventKind.CancelOrder:
                RemoveTracked(e.OrderId);
                break;
            case MarketEventKind.Fill:
            {
                var order = _orders[e.OrderId];
                if (e.Size >= order.Size)
                {
                    RemoveTracked(e.OrderId);
                }
                else
                {
                    _orders[e.OrderId] = (order.Side, order.PriceRaw, order.Size - e.Size);
                }
                break;
            }
            case MarketEventKind.ModifyOrder:
            {
                var order = _orders[e.OrderId];
                if (order.PriceRaw != e.Price.RawNano)
                {
                    DequeueFromPrice(order.Side, order.PriceRaw, e.OrderId);
                    EnqueueAtPrice(order.Side, e.Price.RawNano, e.OrderId);
                }
                _orders[e.OrderId] = (order.Side, e.Price.RawNano, e.Size);
                break;
            }
        }
    }

    private void RemoveTracked(ulong id)
    {
        var order = _orders[id];
        _orders.Remove(id);
        int idx = _liveIndex[id];
        int last = _liveIds.Count - 1;
        ulong moved = _liveIds[last];
        _liveIds[idx] = moved;
        _liveIndex[moved] = idx;
        _liveIds.RemoveAt(last);
        _liveIndex.Remove(id);
        DequeueFromPrice(order.Side, order.PriceRaw, id);
    }

    private Dictionary<long, List<ulong>> Queues(Side side) => side == Side.Bid ? _bidQueues : _askQueues;

    private void EnqueueAtPrice(Side side, long priceRaw, ulong id)
    {
        var queues = Queues(side);
        if (!queues.TryGetValue(priceRaw, out var q))
        {
            q = new List<ulong>(8);
            queues.Add(priceRaw, q);
        }
        q.Add(id);
    }

    private void DequeueFromPrice(Side side, long priceRaw, ulong id)
    {
        var q = Queues(side)[priceRaw];
        q.Remove(id);
        if (q.Count == 0)
        {
            Queues(side).Remove(priceRaw);
        }
    }
}
