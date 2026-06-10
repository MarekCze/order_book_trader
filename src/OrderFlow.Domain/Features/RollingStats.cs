using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Trailing-window mean / sample standard deviation / z-score of an integer-valued
/// series (e.g. F5's top-5 depth vs its trailing 30-minute distribution), aggregated
/// into one-second buckets. Accumulators are integral (exact, deterministic); doubles
/// appear only in derived queries. Values must stay below ~1e6 so the sum of squares
/// fits a long over the window. Window semantics match <see cref="FlowWindowRing"/>:
/// a sample ages out when exactly windowSeconds old.
/// </summary>
public sealed class RollingStats
{
    private readonly struct Bucket(long count, long sum, long sumSq)
    {
        public readonly long Count = count;
        public readonly long Sum = sum;
        public readonly long SumSq = sumSq;
    }

    private readonly Bucket[] _buckets;
    private long _currentSecond = long.MinValue;
    private long _count;
    private long _sum;
    private long _sumSq;

    public RollingStats(int windowSeconds)
    {
        _buckets = new Bucket[windowSeconds];
    }

    public long Count => _count;

    public double? Mean => _count > 0 ? (double)_sum / _count : null;

    public double? SampleStd
    {
        get
        {
            if (_count < 2)
            {
                return null;
            }
            double variance = (_sumSq - (double)_sum * _sum / _count) / (_count - 1);
            return variance > 0 ? Math.Sqrt(variance) : 0.0;
        }
    }

    public void Add(Timestamp ts, long value)
    {
        Advance(ts);
        int idx = Mod(_currentSecond);
        var b = _buckets[idx];
        _buckets[idx] = new Bucket(b.Count + 1, b.Sum + value, b.SumSq + value * value);
        _count++;
        _sum += value;
        _sumSq += value * value;
    }

    /// <summary>Evicts aged buckets without adding a sample (call before reading after idle gaps).</summary>
    public void Advance(Timestamp ts)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        if (_currentSecond == long.MinValue || sec - _currentSecond >= _buckets.Length)
        {
            Array.Clear(_buckets);
            _count = 0;
            _sum = 0;
            _sumSq = 0;
            _currentSecond = sec;
            return;
        }
        if (sec <= _currentSecond)
        {
            return;
        }
        for (long s = _currentSecond + 1; s <= sec; s++)
        {
            int idx = Mod(s); // slot held second s - window, now aging out
            var b = _buckets[idx];
            _count -= b.Count;
            _sum -= b.Sum;
            _sumSq -= b.SumSq;
            _buckets[idx] = default;
        }
        _currentSecond = sec;
    }

    public double? ZScore(double value)
    {
        var std = SampleStd;
        return std is > 0 ? (value - Mean!.Value) / std.Value : null;
    }

    private int Mod(long second) => (int)((second % _buckets.Length + _buckets.Length) % _buckets.Length);
}
