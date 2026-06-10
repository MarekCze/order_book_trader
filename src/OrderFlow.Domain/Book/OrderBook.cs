using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Book;

/// <summary>
/// Order-by-ID limit order book for a single instrument, reconstructed from MBO events.
/// Derives MBP aggregates (top-k levels per side) on demand. Deterministic by construction:
/// levels live in <see cref="SortedDictionary{TKey,TValue}"/> (ordered iteration only) and
/// per-order state in a hash map whose iteration order is never observed.
///
/// Semantics implemented (see PR description for the assumptions behind them):
///  - Cancel/Fill reduce the order by the event Size and remove it when depleted
///    (event Size of 0 is treated as a full cancel, defensively).
///  - Modify carries the NEW absolute price/size.
///  - Modify of an unknown order is upserted as an Add (mid-session stream start); counted.
///  - Cancel/Fill of an unknown order is ignored; counted.
///  - Trade ('T') never mutates the book — the paired Fill ('F') records do.
/// </summary>
public sealed class OrderBook
{
    private struct RestingOrder
    {
        public long PriceRaw;
        public uint Size;
        public Side Side;
    }

    private struct Level
    {
        public long Size;
        public int OrderCount;
    }

    private static readonly IComparer<long> DescendingPrice =
        Comparer<long>.Create(static (a, b) => b.CompareTo(a));

    private readonly Dictionary<ulong, RestingOrder> _orders = new(1 << 14);
    // Key order: bids descending, asks ascending — so the first entry is always the best.
    private readonly SortedDictionary<long, Level> _bids = new(DescendingPrice);
    private readonly SortedDictionary<long, Level> _asks = new();

    private long _bidTotal;
    private long _askTotal;
    // Cached best prices so per-event BBO reads don't construct tree enumerators
    // (SortedDictionary enumerators allocate an internal stack).
    private long _bestBid;
    private bool _hasBestBid;
    private long _bestAsk;
    private bool _hasBestAsk;

    public BookAnomalyCounters Anomalies { get; } = new();

    public int OrderCount => _orders.Count;

    public void Apply(in MarketEvent e)
    {
        switch (e.Kind)
        {
            case MarketEventKind.AddOrder:
                ApplyAdd(e.OrderId, e.Side, e.Price.RawNano, e.Size, countDuplicate: true);
                break;
            case MarketEventKind.CancelOrder:
                ApplyReduce(e.OrderId, e.Size, isFill: false);
                break;
            case MarketEventKind.ModifyOrder:
                ApplyModify(e.OrderId, e.Side, e.Price.RawNano, e.Size);
                break;
            case MarketEventKind.Fill:
                ApplyReduce(e.OrderId, e.Size, isFill: true);
                break;
            case MarketEventKind.BookClear:
                Clear();
                break;
            case MarketEventKind.Trade:
            case MarketEventKind.SnapshotEnd:
            case MarketEventKind.None:
                break;
        }
    }

    public void Clear()
    {
        _orders.Clear();
        _bids.Clear();
        _asks.Clear();
        _bidTotal = 0;
        _askTotal = 0;
        _hasBestBid = false;
        _hasBestAsk = false;
    }

    public bool TryGetBest(Side side, out PriceLevel best)
    {
        if (side == Side.Bid && _hasBestBid)
        {
            var lvl = _bids[_bestBid];
            best = new PriceLevel(new Price(_bestBid), lvl.Size, lvl.OrderCount);
            return true;
        }
        if (side == Side.Ask && _hasBestAsk)
        {
            var lvl = _asks[_bestAsk];
            best = new PriceLevel(new Price(_bestAsk), lvl.Size, lvl.OrderCount);
            return true;
        }
        best = default;
        return false;
    }

    /// <summary>Spread in ticks; false when either side is empty.</summary>
    public bool TryGetSpreadTicks(TickSize tick, out long spreadTicks)
    {
        if (_hasBestBid && _hasBestAsk)
        {
            spreadTicks = (_bestAsk - _bestBid) / tick.RawNano;
            return true;
        }
        spreadTicks = 0;
        return false;
    }

    /// <summary>
    /// Copies up to <paramref name="dest"/>.Length best levels (best first) into the span;
    /// returns the number written. MBP-10 = a span of length 10.
    /// </summary>
    public int CopyTopLevels(Side side, Span<PriceLevel> dest)
    {
        int i = 0;
        foreach (var kv in SideLevels(side))
        {
            if (i >= dest.Length)
            {
                break;
            }
            dest[i++] = new PriceLevel(new Price(kv.Key), kv.Value.Size, kv.Value.OrderCount);
        }
        return i;
    }

    public long DisplayedTotal(Side side) => side == Side.Bid ? _bidTotal : _askTotal;

    public long DisplayedAt(Side side, Price price) =>
        SideLevels(side).TryGetValue(price.RawNano, out var lvl) ? lvl.Size : 0;

    public int LevelCount(Side side) => SideLevels(side).Count;

    /// <summary>
    /// O(n) recomputation from per-order state, for tests/verification only —
    /// must always equal the incrementally maintained aggregates.
    /// </summary>
    public (long BidTotal, long AskTotal, int OrderCount) RecomputeTotalsForVerification()
    {
        long bid = 0, ask = 0;
        foreach (var o in _orders.Values)
        {
            if (o.Side == Side.Bid)
            {
                bid += o.Size;
            }
            else
            {
                ask += o.Size;
            }
        }
        return (bid, ask, _orders.Count);
    }

    private SortedDictionary<long, Level> SideLevels(Side side) => side == Side.Bid ? _bids : _asks;

    private void ApplyAdd(ulong orderId, Side side, long priceRaw, uint size, bool countDuplicate)
    {
        if (side == Side.None)
        {
            return; // implied/uncrossable records; nothing to rest
        }
        if (_orders.TryGetValue(orderId, out var existing))
        {
            if (countDuplicate)
            {
                Anomalies.DuplicateAdd++;
            }
            RemoveFromLevel(existing.Side, existing.PriceRaw, existing.Size, orderRemoved: true);
            _orders.Remove(orderId);
        }
        _orders.Add(orderId, new RestingOrder { PriceRaw = priceRaw, Size = size, Side = side });
        AddToLevel(side, priceRaw, size);
    }

    private void ApplyReduce(ulong orderId, uint eventSize, bool isFill)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            if (isFill)
            {
                Anomalies.FillUnknownOrder++;
            }
            else
            {
                Anomalies.CancelUnknownOrder++;
            }
            return;
        }
        uint qty = eventSize == 0 ? order.Size : Math.Min(eventSize, order.Size);
        if (qty >= order.Size)
        {
            _orders.Remove(orderId);
            RemoveFromLevel(order.Side, order.PriceRaw, order.Size, orderRemoved: true);
        }
        else
        {
            order.Size -= qty;
            _orders[orderId] = order;
            RemoveFromLevel(order.Side, order.PriceRaw, qty, orderRemoved: false);
        }
    }

    private void ApplyModify(ulong orderId, Side side, long newPriceRaw, uint newSize)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            Anomalies.ModifyUnknownOrder++;
            ApplyAdd(orderId, side, newPriceRaw, newSize, countDuplicate: false);
            return;
        }
        if (side != Side.None && side != order.Side)
        {
            Anomalies.SideMismatch++;
            RemoveFromLevel(order.Side, order.PriceRaw, order.Size, orderRemoved: true);
            _orders.Remove(orderId);
            ApplyAdd(orderId, side, newPriceRaw, newSize, countDuplicate: false);
            return;
        }
        if (order.PriceRaw == newPriceRaw)
        {
            long delta = (long)newSize - order.Size;
            if (delta != 0)
            {
                AdjustLevelSize(order.Side, order.PriceRaw, delta);
            }
        }
        else
        {
            RemoveFromLevel(order.Side, order.PriceRaw, order.Size, orderRemoved: true);
            AddToLevel(order.Side, newPriceRaw, newSize);
        }
        order.PriceRaw = newPriceRaw;
        order.Size = newSize;
        _orders[orderId] = order;
    }

    private void AddToLevel(Side side, long priceRaw, uint size)
    {
        var levels = SideLevels(side);
        if (levels.TryGetValue(priceRaw, out var lvl))
        {
            lvl.Size += size;
            lvl.OrderCount++;
            levels[priceRaw] = lvl;
        }
        else
        {
            levels.Add(priceRaw, new Level { Size = size, OrderCount = 1 });
            if (side == Side.Bid)
            {
                if (!_hasBestBid || priceRaw > _bestBid)
                {
                    _bestBid = priceRaw;
                    _hasBestBid = true;
                }
            }
            else
            {
                if (!_hasBestAsk || priceRaw < _bestAsk)
                {
                    _bestAsk = priceRaw;
                    _hasBestAsk = true;
                }
            }
        }
        if (side == Side.Bid)
        {
            _bidTotal += size;
        }
        else
        {
            _askTotal += size;
        }
    }

    private void RemoveFromLevel(Side side, long priceRaw, uint size, bool orderRemoved)
    {
        var levels = SideLevels(side);
        var lvl = levels[priceRaw]; // missing key here means internal corruption — let it throw
        lvl.Size -= size;
        if (orderRemoved)
        {
            lvl.OrderCount--;
        }
        if (lvl.OrderCount <= 0)
        {
            levels.Remove(priceRaw);
            RefreshBestAfterLevelRemoval(side, priceRaw);
        }
        else
        {
            levels[priceRaw] = lvl;
        }
        if (side == Side.Bid)
        {
            _bidTotal -= size;
        }
        else
        {
            _askTotal -= size;
        }
    }

    private void AdjustLevelSize(Side side, long priceRaw, long delta)
    {
        var levels = SideLevels(side);
        var lvl = levels[priceRaw];
        lvl.Size += delta;
        levels[priceRaw] = lvl;
        if (side == Side.Bid)
        {
            _bidTotal += delta;
        }
        else
        {
            _askTotal += delta;
        }
    }

    private void RefreshBestAfterLevelRemoval(Side side, long removedPriceRaw)
    {
        if (side == Side.Bid)
        {
            if (_hasBestBid && _bestBid == removedPriceRaw)
            {
                _hasBestBid = TryFirstKey(_bids, out _bestBid);
            }
        }
        else
        {
            if (_hasBestAsk && _bestAsk == removedPriceRaw)
            {
                _hasBestAsk = TryFirstKey(_asks, out _bestAsk);
            }
        }
    }

    private static bool TryFirstKey(SortedDictionary<long, Level> levels, out long key)
    {
        foreach (var kv in levels)
        {
            key = kv.Key;
            return true;
        }
        key = 0;
        return false;
    }
}
