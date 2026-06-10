using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>Per-event add/cancel volume increments from the book diff (F16 inputs).</summary>
public readonly record struct LiquiditySample(
    long BidAddVolume,
    long BidCancelVolume,
    long AskAddVolume,
    long AskCancelVolume)
{
    internal LiquiditySample Plus(in LiquiditySample s) => new(
        BidAddVolume + s.BidAddVolume, BidCancelVolume + s.BidCancelVolume,
        AskAddVolume + s.AskAddVolume, AskCancelVolume + s.AskCancelVolume);

    internal LiquiditySample Minus(in LiquiditySample s) => new(
        BidAddVolume - s.BidAddVolume, BidCancelVolume - s.BidCancelVolume,
        AskAddVolume - s.AskAddVolume, AskCancelVolume - s.AskCancelVolume);
}

/// <summary>
/// One-second bucket ring with running add/cancel totals per trailing window — the
/// liquidity twin of <see cref="FlowWindowRing"/> (same window semantics: a sample ages
/// out at exactly w seconds; gaps beyond the ring reset it). Kept as a flat duplicate of
/// that pattern rather than a generic abstraction to keep the hot path allocation-free.
/// </summary>
public sealed class LiquidityWindowRing
{
    private readonly int[] _windows;
    private readonly LiquiditySample[] _buckets;
    private readonly LiquiditySample[] _windowSums;
    private long _currentSecond = long.MinValue;

    public LiquidityWindowRing(int[] windowSeconds)
    {
        _windows = (int[])windowSeconds.Clone();
        _buckets = new LiquiditySample[_windows.Max()];
        _windowSums = new LiquiditySample[_windows.Length];
    }

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
                long evictSecond = s - _windows[i];
                if (evictSecond >= s - _buckets.Length)
                {
                    _windowSums[i] = _windowSums[i].Minus(_buckets[Mod(evictSecond)]);
                }
            }
            _buckets[Mod(s)] = default;
        }
        _currentSecond = sec;
    }

    public void Add(in LiquiditySample sample)
    {
        _buckets[Mod(_currentSecond)] = _buckets[Mod(_currentSecond)].Plus(sample);
        for (int i = 0; i < _windowSums.Length; i++)
        {
            _windowSums[i] = _windowSums[i].Plus(sample);
        }
    }

    public LiquiditySample WindowSum(int windowIndex) => _windowSums[windowIndex];

    private int Mod(long second) => (int)((second % _buckets.Length + _buckets.Length) % _buckets.Length);
}

/// <summary>
/// Last-value-per-second series answering "what was the value exactly N seconds ago"
/// (F21's lagged depth comparison). Carry-forward fills quiet seconds; null before
/// history exists. O(1) per write and query.
/// </summary>
public sealed class TimeLaggedValue
{
    private readonly (long Second, long Value)[] _ring;
    private long _currentSecond = long.MinValue;
    private long _firstSecond = long.MinValue;
    private long _lastValue;

    public TimeLaggedValue(int lagSeconds)
    {
        _ring = new (long, long)[lagSeconds + 1];
    }

    public void Write(Timestamp ts, long value)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        if (_currentSecond == long.MinValue)
        {
            _firstSecond = sec;
        }
        else if (sec > _currentSecond)
        {
            // carry the previous value through quiet seconds (capped at ring length)
            for (long s = Math.Max(_currentSecond + 1, sec - _ring.Length + 1); s < sec; s++)
            {
                _ring[Mod(s)] = (s, _lastValue);
            }
        }
        if (sec >= _currentSecond)
        {
            _currentSecond = sec;
        }
        _ring[Mod(_currentSecond)] = (_currentSecond, value);
        _lastValue = value;
    }

    /// <summary>Value as of <paramref name="lagSeconds"/> before the last write; null before history.</summary>
    public long? Lagged(int lagSeconds)
    {
        if (_currentSecond == long.MinValue)
        {
            return null;
        }
        long sec = _currentSecond - lagSeconds;
        if (sec < _firstSecond)
        {
            return null;
        }
        var entry = _ring[Mod(sec)];
        return entry.Second == sec ? entry.Value : null;
    }

    private int Mod(long second) => (int)((second % _ring.Length + _ring.Length) % _ring.Length);
}
