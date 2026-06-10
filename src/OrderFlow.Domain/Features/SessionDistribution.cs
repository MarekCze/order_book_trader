namespace OrderFlow.Domain.Features;

/// <summary>
/// THE shared session percentile/z-score component (CLAUDE.md mandates exactly one).
/// Holds a rolling within-session distribution of integer-valued samples (contract
/// counts, deltas, sizes — everything the rulebook percentiles over is integral).
///
/// Queries return null until <c>minSamples</c> live samples have accumulated; before
/// that they fall back to the prior session's final distribution when one was seeded
/// via <see cref="StartNextSession"/>. Percentiles are exact (Fenwick tree over the
/// clamped value range, O(log n) add/query); z-scores use Welford running moments with
/// the sample (n−1) standard deviation. Fully deterministic.
/// </summary>
public sealed class SessionDistribution
{
    private sealed class State
    {
        public readonly long Min;
        public readonly long[] Tree;   // Fenwick, 1-based over (max-min+1) bins
        public long Count;
        public double Mean;
        public double M2;

        public State(long min, long max)
        {
            Min = min;
            Tree = new long[max - min + 2];
        }

        public void Add(long value)
        {
            for (int i = (int)(value - Min) + 1; i < Tree.Length; i += i & -i)
            {
                Tree[i]++;
            }
            Count++;
            double d = value - Mean;
            Mean += d / Count;
            M2 += d * (value - Mean);
        }

        public long CountAtOrBelow(long value)
        {
            long sum = 0;
            for (int i = (int)(value - Min) + 1; i > 0; i -= i & -i)
            {
                sum += Tree[i];
            }
            return sum;
        }

        /// <summary>Smallest value whose cumulative count reaches <paramref name="target"/>.</summary>
        public long ValueAtCount(long target)
        {
            int idx = 0;
            long remaining = target;
            for (int step = HighestPow2(Tree.Length - 1); step > 0; step >>= 1)
            {
                int next = idx + step;
                if (next < Tree.Length && Tree[next] < remaining)
                {
                    remaining -= Tree[next];
                    idx = next;
                }
            }
            return Min + idx; // idx is the largest prefix strictly below target; bin idx+1 (0-based idx) holds it
        }

        private static int HighestPow2(int n)
        {
            int p = 1;
            while (p << 1 <= n)
            {
                p <<= 1;
            }
            return p;
        }
    }

    private readonly long _min;
    private readonly long _max;
    private readonly int _minSamples;
    private readonly State _live;
    private readonly State? _baseline;

    public SessionDistribution(long minValue, long maxValue, int minSamples = 200)
    {
        if (maxValue <= minValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue), "Range must be non-empty.");
        }
        _min = minValue;
        _max = maxValue;
        _minSamples = minSamples;
        _live = new State(minValue, maxValue);
    }

    private SessionDistribution(SessionDistribution prior)
        : this(prior._min, prior._max, prior._minSamples)
    {
        // Prior session's final distribution becomes the fallback — but only if it was
        // itself ready; an unready prior falls back to ITS baseline (two sessions ago).
        _baseline = prior._live.Count >= prior._minSamples ? prior._live : prior._baseline;
    }

    /// <summary>Live samples added this session.</summary>
    public long Count => _live.Count;

    /// <summary>True once queries can answer — live samples reached the minimum, or a baseline exists.</summary>
    public bool IsReady => _live.Count >= _minSamples || _baseline is not null;

    public void Add(long value)
    {
        _live.Add(Math.Clamp(value, _min, _max));
    }

    /// <summary>The value at quantile <paramref name="p"/> ∈ (0, 1]; null until ready.</summary>
    public long? Quantile(double p)
    {
        var s = ActiveState();
        if (s is null)
        {
            return null;
        }
        long target = Math.Clamp((long)Math.Ceiling(p * s.Count), 1, s.Count);
        return s.ValueAtCount(target);
    }

    /// <summary>Fraction of samples ≤ <paramref name="value"/>; null until ready.</summary>
    public double? PercentileRank(long value)
    {
        var s = ActiveState();
        if (s is null)
        {
            return null;
        }
        return (double)s.CountAtOrBelow(Math.Clamp(value, _min, _max)) / s.Count;
    }

    /// <summary>Z-score against the active distribution; null until ready or when variance is zero.</summary>
    public double? ZScore(double value)
    {
        var s = ActiveState();
        if (s is null || s.Count < 2)
        {
            return null;
        }
        double std = Math.Sqrt(s.M2 / (s.Count - 1));
        return std > 0 ? (value - s.Mean) / std : null;
    }

    /// <summary>
    /// Session rollover: returns a fresh distribution whose fallback baseline is this
    /// session's final distribution. The prior instance must not be added to afterwards.
    /// </summary>
    public SessionDistribution StartNextSession() => new(this);

    private State? ActiveState()
    {
        if (_live.Count >= _minSamples)
        {
            return _live;
        }
        return _baseline;
    }
}
