using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>Windowed add/cancel totals per side (F16 numerators/denominators).</summary>
public readonly record struct LiquidityAggregate(
    long BidAddVolume,
    long BidCancelVolume,
    long AskAddVolume,
    long AskCancelVolume);

/// <summary>
/// Liquidity-dynamics state (rulebook 2.3) reconstructed from MBP-10 book diffs — there
/// are no order events at this schema level, so everything here is the documented
/// CLAUDE.md heuristic, not ground truth:
///
///  - Each event's top-10 state is diffed against the previous state per side. A
///    displayed-size DECREASE at a price is classified as traded up to the volume of
///    trade records printed at that price within the attribution window (budget model:
///    each trade credits its price, each decrease consumes), and cancelled for the
///    remainder (F16). An INCREASE is add volume; an increase at a price that already
///    had size is additionally a refresh event (F18).
///  - F17/F22 track per-price rolling maxima of displayed size and per-price traded
///    volume over the replenish window, queryable at any price (detectors in M4 ask
///    about their defended level; the journal asks about the current best).
///  - F21 samples the displayed size of the levels beyond the best each event and
///    compares against the value a configurable lag ago.
///
/// Snapshot-flagged records and the first state after a clear are baselines: they
/// replace the previous state without generating add/cancel volume.
/// </summary>
public sealed class LiquidityDynamicsTracker
{
    private readonly FeatureEngineOptions _opts;
    private readonly LiquidityWindowRing _ring;
    private readonly VolumeByPriceWindow _tradedAtPrice;
    private readonly Dictionary<long, MonotonicWindowExtreme> _maxDisplayedBid = new();
    private readonly Dictionary<long, MonotonicWindowExtreme> _maxDisplayedAsk = new();
    private readonly Dictionary<long, Queue<long>> _refreshSecondsBid = new();
    private readonly Dictionary<long, Queue<long>> _refreshSecondsAsk = new();
    private readonly Dictionary<long, (long Second, long Volume)> _tradeBudgets = new();
    private readonly TimeLaggedValue _beyondBestBid;
    private readonly TimeLaggedValue _beyondBestAsk;
    private long _lastBeyondBid;
    private long _lastBeyondAsk;
    private BookLevels _prev;
    private bool _hasPrev;

    public LiquidityDynamicsTracker(TickSize tick, FeatureEngineOptions options)
    {
        _opts = options;
        _ring = new LiquidityWindowRing(options.FlowWindowsSeconds);
        _tradedAtPrice = new VolumeByPriceWindow(options.ReplenishWindowSeconds);
        _beyondBestBid = new TimeLaggedValue(options.DepthChangeLagSeconds);
        _beyondBestAsk = new TimeLaggedValue(options.DepthChangeLagSeconds);
    }

    public void OnEvent(in MarketEvent e, BookStateTracker tracker)
    {
        long sec = (long)(e.TsEvent.UnixNanos / 1_000_000_000UL);
        _ring.Advance(e.TsEvent);

        if (e.Kind == MarketEventKind.Trade)
        {
            CreditTradeBudget(e.Price.RawNano, e.Size, sec);
            _tradedAtPrice.Add(e.TsEvent, e.Price, e.Size);
        }

        var current = tracker.Levels;
        if (_hasPrev && !e.IsSnapshot)
        {
            long bidAdd = 0, bidCancel = 0, askAdd = 0, askCancel = 0;
            DiffSide(Side.Bid, in _prev, in current, sec, e.TsEvent, ref bidAdd, ref bidCancel);
            DiffSide(Side.Ask, in _prev, in current, sec, e.TsEvent, ref askAdd, ref askCancel);
            if ((bidAdd | bidCancel | askAdd | askCancel) != 0)
            {
                _ring.Add(new LiquiditySample(bidAdd, bidCancel, askAdd, askCancel));
            }
        }
        else
        {
            // Baseline: still seed the per-price max-displayed trackers.
            TrackDisplayedMaxima(in current, e.TsEvent);
        }
        _prev = current;
        _hasPrev = true;

        _lastBeyondBid = BeyondBestDepth(in current, Side.Bid);
        _lastBeyondAsk = BeyondBestDepth(in current, Side.Ask);
        _beyondBestBid.Write(e.TsEvent, _lastBeyondBid);
        _beyondBestAsk.Write(e.TsEvent, _lastBeyondAsk);
    }

    /// <summary>Forget the previous state (book clear, session rollover): next state is a baseline.</summary>
    public void Reset() => _hasPrev = false;

    public LiquidityAggregate WindowSum(int windowIndex)
    {
        var s = _ring.WindowSum(windowIndex);
        return new LiquidityAggregate(s.BidAddVolume, s.BidCancelVolume, s.AskAddVolume, s.AskCancelVolume);
    }

    /// <summary>F16: cancel ÷ add volume for a side over a window; null when nothing was added.</summary>
    public double? PullRatio(Side side, int windowIndex)
    {
        var agg = WindowSum(windowIndex);
        (long cancel, long add) = side == Side.Bid
            ? (agg.BidCancelVolume, agg.BidAddVolume)
            : (agg.AskCancelVolume, agg.AskAddVolume);
        return add > 0 ? (double)cancel / add : null;
    }

    /// <summary>F17: traded(P) ÷ max displayed(P) over the replenish window; null when P was never displayed.</summary>
    public double? ReplenishRatio(Side side, Price price, Timestamp now)
    {
        long? max = MaxDisplayed(side, price, now);
        if (max is not > 0)
        {
            return null;
        }
        _tradedAtPrice.Advance(now);
        return (double)_tradedAtPrice.VolumeAt(price) / max.Value;
    }

    /// <summary>F18: displayed-size increases at P (at a price that already had size) within the window.</summary>
    public int RefreshCount(Side side, Price price, Timestamp now)
    {
        var map = side == Side.Bid ? _refreshSecondsBid : _refreshSecondsAsk;
        if (!map.TryGetValue(price.RawNano, out var queue))
        {
            return 0;
        }
        long cutoff = (long)(now.UnixNanos / 1_000_000_000UL) - _opts.ReplenishWindowSeconds;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }
        return queue.Count;
    }

    /// <summary>F22: true when displayed size at P dropped more than the vanish ratio of its window max
    /// without trades covering the disappearance.</summary>
    public bool VanishFlag(Side side, Price price, long currentDisplayed, Timestamp now)
    {
        long? max = MaxDisplayed(side, price, now);
        if (max is not > 0)
        {
            return false;
        }
        _tradedAtPrice.Advance(now);
        long untraded = max.Value - currentDisplayed - _tradedAtPrice.VolumeAt(price);
        return (double)untraded / max.Value > _opts.VanishDropRatio;
    }

    /// <summary>F21: %Δ of beyond-best displayed size (levels 2..N+1) vs the configured lag; null without history.</summary>
    public double? DepthChangeBeyondBest(Side side)
    {
        var series = side == Side.Bid ? _beyondBestBid : _beyondBestAsk;
        long now = side == Side.Bid ? _lastBeyondBid : _lastBeyondAsk;
        long? lagged = series.Lagged(_opts.DepthChangeLagSeconds);
        if (lagged is not > 0)
        {
            return null;
        }
        return (double)(now - lagged.Value) / lagged.Value;
    }

    private long? MaxDisplayed(Side side, Price price, Timestamp now)
    {
        var map = side == Side.Bid ? _maxDisplayedBid : _maxDisplayedAsk;
        if (!map.TryGetValue(price.RawNano, out var extreme))
        {
            return null;
        }
        extreme.Advance(now);
        return extreme.Extreme;
    }

    private void DiffSide(
        Side side, in BookLevels prev, in BookLevels current, long sec, Timestamp ts,
        ref long addVolume, ref long cancelVolume)
    {
        // Both snapshots are sorted away from the touch (bids descending, asks ascending);
        // a single merge pass yields per-price size deltas.
        Span<(long Price, long Size)> p = stackalloc (long, long)[BookLevels.Depth];
        Span<(long Price, long Size)> c = stackalloc (long, long)[BookLevels.Depth];
        int np = Extract(in prev, side, p);
        int nc = Extract(in current, side, c);
        int i = 0, j = 0;
        while (i < np || j < nc)
        {
            if (j >= nc)
            {
                // price disappeared entirely
                Attribute(side, p[i].Price, -p[i].Size, existedBefore: true, sec, ts, ref addVolume, ref cancelVolume);
                i++;
            }
            else if (i >= np)
            {
                // brand-new price level
                Attribute(side, c[j].Price, c[j].Size, existedBefore: false, sec, ts, ref addVolume, ref cancelVolume);
                TrackMax(side, c[j].Price, c[j].Size, ts);
                j++;
            }
            else if (p[i].Price == c[j].Price)
            {
                long delta = c[j].Size - p[i].Size;
                if (delta != 0)
                {
                    Attribute(side, c[j].Price, delta, existedBefore: true, sec, ts, ref addVolume, ref cancelVolume);
                }
                TrackMax(side, c[j].Price, c[j].Size, ts);
                i++;
                j++;
            }
            else if (CloserToTouch(side, p[i].Price, c[j].Price))
            {
                Attribute(side, p[i].Price, -p[i].Size, existedBefore: true, sec, ts, ref addVolume, ref cancelVolume);
                i++;
            }
            else
            {
                Attribute(side, c[j].Price, c[j].Size, existedBefore: false, sec, ts, ref addVolume, ref cancelVolume);
                TrackMax(side, c[j].Price, c[j].Size, ts);
                j++;
            }
        }
    }

    private void Attribute(
        Side side, long priceRaw, long delta, bool existedBefore, long sec, Timestamp ts,
        ref long addVolume, ref long cancelVolume)
    {
        if (delta > 0)
        {
            addVolume += delta;
            if (existedBefore)
            {
                var map = side == Side.Bid ? _refreshSecondsBid : _refreshSecondsAsk;
                if (!map.TryGetValue(priceRaw, out var queue))
                {
                    queue = new Queue<long>();
                    map.Add(priceRaw, queue);
                }
                queue.Enqueue(sec);
            }
        }
        else
        {
            long decrease = -delta;
            long traded = ConsumeTradeBudget(priceRaw, decrease, sec);
            cancelVolume += decrease - traded;
        }
    }

    private void TrackDisplayedMaxima(in BookLevels levels, Timestamp ts)
    {
        Span<(long Price, long Size)> tmp = stackalloc (long, long)[BookLevels.Depth];
        foreach (var side in stackalloc[] { Side.Bid, Side.Ask })
        {
            int n = Extract(in levels, side, tmp);
            for (int k = 0; k < n; k++)
            {
                TrackMax(side, tmp[k].Price, tmp[k].Size, ts);
            }
        }
    }

    private void TrackMax(Side side, long priceRaw, long size, Timestamp ts)
    {
        var map = side == Side.Bid ? _maxDisplayedBid : _maxDisplayedAsk;
        if (!map.TryGetValue(priceRaw, out var extreme))
        {
            extreme = new MonotonicWindowExtreme(_opts.ReplenishWindowSeconds, trackMax: true);
            map.Add(priceRaw, extreme);
        }
        extreme.Add(ts, size);
    }

    private void CreditTradeBudget(long priceRaw, long size, long sec)
    {
        _tradeBudgets[priceRaw] = _tradeBudgets.TryGetValue(priceRaw, out var entry) && sec - entry.Second <= _opts.AttributionWindowSeconds
            ? (sec, entry.Volume + size)
            : (sec, size);
    }

    private long ConsumeTradeBudget(long priceRaw, long amount, long sec)
    {
        if (!_tradeBudgets.TryGetValue(priceRaw, out var entry) || sec - entry.Second > _opts.AttributionWindowSeconds)
        {
            return 0;
        }
        long take = Math.Min(amount, entry.Volume);
        _tradeBudgets[priceRaw] = (entry.Second, entry.Volume - take);
        return take;
    }

    private long BeyondBestDepth(in BookLevels levels, Side side)
    {
        long sum = 0;
        for (int i = 1; i <= _opts.DepthChangeLevels && i < BookLevels.Depth; i++)
        {
            var lvl = levels[i];
            sum += side == Side.Bid ? (lvl.HasBid ? lvl.BidSize : 0) : (lvl.HasAsk ? lvl.AskSize : 0);
        }
        return sum;
    }

    private static int Extract(in BookLevels levels, Side side, Span<(long, long)> dest)
    {
        int n = 0;
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var lvl = levels[i];
            if (side == Side.Bid)
            {
                if (lvl.HasBid)
                {
                    dest[n++] = (lvl.BidPrice.RawNano, lvl.BidSize);
                }
            }
            else if (lvl.HasAsk)
            {
                dest[n++] = (lvl.AskPrice.RawNano, lvl.AskSize);
            }
        }
        return n;
    }

    /// <summary>True when a sorts before b in that side's book ordering (bids descend, asks ascend).</summary>
    private static bool CloserToTouch(Side side, long a, long b) =>
        side == Side.Bid ? a > b : a < b;
}
