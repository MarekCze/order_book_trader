using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>Per-event flow increments, aggregated into one-second buckets.</summary>
public readonly record struct FlowSample(
    long BuyVolume,
    long SellVolume,
    int TradeCount,
    int LargePrintCount,
    int SweepCount);

/// <summary>Flow totals over a trailing window (or arbitrary second range).</summary>
public readonly record struct FlowAggregate(
    long BuyVolume,
    long SellVolume,
    long TradeCount,
    long LargePrintCount,
    long SweepCount)
{
    public long Delta => BuyVolume - SellVolume;

    public long Volume => BuyVolume + SellVolume;

    internal FlowAggregate Plus(in FlowSample s) => new(
        BuyVolume + s.BuyVolume, SellVolume + s.SellVolume,
        TradeCount + s.TradeCount, LargePrintCount + s.LargePrintCount, SweepCount + s.SweepCount);

    internal FlowAggregate Plus(in FlowAggregate a) => new(
        BuyVolume + a.BuyVolume, SellVolume + a.SellVolume,
        TradeCount + a.TradeCount, LargePrintCount + a.LargePrintCount, SweepCount + a.SweepCount);

    internal FlowAggregate Minus(in FlowAggregate a) => new(
        BuyVolume - a.BuyVolume, SellVolume - a.SellVolume,
        TradeCount - a.TradeCount, LargePrintCount - a.LargePrintCount, SweepCount - a.SweepCount);
}

/// <summary>
/// One-second bucket ring maintaining running flow totals over several trailing windows
/// simultaneously (rulebook w ∈ {10s, 30s, 60s, 300s}). A window of w seconds covers the
/// half-open second range (current − w, current], i.e. a sample ages out when it is
/// exactly w seconds old. O(1) per event; advancing across a gap costs O(gap), capped at
/// the ring length (larger gaps reset the ring). All integer arithmetic — deterministic.
/// Time must be non-decreasing; an earlier timestamp is treated as the current second.
/// </summary>
public sealed class FlowWindowRing
{
    private readonly int[] _windows;
    private readonly FlowAggregate[] _buckets; // index = second % ring length
    private readonly FlowAggregate[] _windowSums;
    private long _currentSecond = long.MinValue;

    public FlowWindowRing(int[] windowSeconds)
    {
        if (windowSeconds.Length == 0)
        {
            throw new ArgumentException("At least one window required.", nameof(windowSeconds));
        }
        _windows = (int[])windowSeconds.Clone();
        _buckets = new FlowAggregate[_windows.Max()];
        _windowSums = new FlowAggregate[_windows.Length];
    }

    /// <summary>Unix second of the current bucket; long.MinValue before the first advance.</summary>
    public long CurrentSecond => _currentSecond;

    public void Advance(Timestamp ts)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        if (_currentSecond == long.MinValue)
        {
            _currentSecond = sec;
            return;
        }
        if (sec <= _currentSecond)
        {
            return;
        }
        if (sec - _currentSecond >= _buckets.Length)
        {
            Array.Clear(_buckets);
            Array.Clear(_windowSums);
            _currentSecond = sec;
            return;
        }
        for (long s = _currentSecond + 1; s <= sec; s++)
        {
            for (int i = 0; i < _windows.Length; i++)
            {
                // While rolling into second s, history still spans [s - ringLength, s - 1];
                // anything older is already zero and must not be subtracted twice.
                long evictSecond = s - _windows[i];
                if (evictSecond >= s - _buckets.Length)
                {
                    _windowSums[i] = _windowSums[i].Minus(_buckets[Mod(evictSecond)]);
                }
            }
            _buckets[Mod(s)] = default; // reuse the slot that held second s - ringLength
        }
        _currentSecond = sec;
    }

    /// <summary>Adds a sample into the current second and all window totals.</summary>
    public void Add(in FlowSample sample)
    {
        if (_currentSecond == long.MinValue)
        {
            throw new InvalidOperationException("Advance must be called before Add.");
        }
        _buckets[Mod(_currentSecond)] = _buckets[Mod(_currentSecond)].Plus(sample);
        for (int i = 0; i < _windowSums.Length; i++)
        {
            _windowSums[i] = _windowSums[i].Plus(sample);
        }
    }

    /// <summary>Running total for the window at <paramref name="windowIndex"/> (constructor order).</summary>
    public FlowAggregate WindowSum(int windowIndex) => _windowSums[windowIndex];

    /// <summary>
    /// Sum over an inclusive second range, clipped to retained history
    /// (the trailing ring-length seconds). On-demand O(range) — not for the hot path.
    /// </summary>
    public FlowAggregate SumSecondsInclusive(long fromSecond, long toSecond)
    {
        if (_currentSecond == long.MinValue)
        {
            return default;
        }
        long from = Math.Max(fromSecond, _currentSecond - _buckets.Length + 1);
        long to = Math.Min(toSecond, _currentSecond);
        var sum = default(FlowAggregate);
        for (long s = from; s <= to; s++)
        {
            sum = sum.Plus(_buckets[Mod(s)]);
        }
        return sum;
    }

    private int Mod(long second) => (int)((second % _buckets.Length + _buckets.Length) % _buckets.Length);
}
