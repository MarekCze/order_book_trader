using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Per-session volume-by-price profile (rulebook LOI math): POC = max bin (ties resolve
/// to the lower price), value area = smallest contiguous expansion around the POC
/// reaching the configured volume fraction (ties expand to the lower side), LVN = local
/// minimum below the LVN ratio of session mean per-price volume, HVN = local maximum at
/// or above the HVN ratio. "Mean per-price volume" averages over DISTINCT TRADED prices;
/// untraded grid gaps inside the range still count as (zero-volume) LVN candidates.
/// Plateaus: the nearest non-equal neighbor on each side decides; range edges are never
/// LVNs (price taper) but may be HVNs. POC updates are incremental; everything else is
/// computed on demand by walking the tick grid (never the dictionary — determinism).
/// </summary>
public sealed class SessionProfile
{
    private readonly TickSize _tick;
    private readonly FeatureEngineOptions _opts;
    private readonly Dictionary<long, long> _volumeByPrice = new();
    private long _pocRaw = long.MinValue;
    private long _pocVolume;
    private long _lowRaw = long.MaxValue;
    private long _highRaw = long.MinValue;

    public SessionProfile(TickSize tick, FeatureEngineOptions options)
    {
        _tick = tick;
        _opts = options;
    }

    public long TotalVolume { get; private set; }

    public Price? Poc => _pocRaw == long.MinValue ? null : new Price(_pocRaw);

    public double? MeanPerPriceVolume =>
        _volumeByPrice.Count > 0 ? (double)TotalVolume / _volumeByPrice.Count : null;

    public void AddTrade(Price price, long size)
    {
        long raw = price.RawNano;
        long volume = _volumeByPrice.GetValueOrDefault(raw) + size;
        _volumeByPrice[raw] = volume;
        TotalVolume += size;
        _lowRaw = Math.Min(_lowRaw, raw);
        _highRaw = Math.Max(_highRaw, raw);
        if (volume > _pocVolume || (volume == _pocVolume && raw < _pocRaw))
        {
            _pocVolume = volume;
            _pocRaw = raw;
        }
    }

    public long VolumeAt(Price price) => _volumeByPrice.GetValueOrDefault(price.RawNano);

    /// <summary>Smallest contiguous volume fraction around the POC; null on an empty profile.</summary>
    public (Price Vah, Price Val)? ValueArea()
    {
        if (_pocRaw == long.MinValue)
        {
            return null;
        }
        double target = _opts.ValueAreaFraction * TotalVolume;
        long lo = _pocRaw, hi = _pocRaw;
        long covered = _pocVolume;
        while (covered < target)
        {
            long below = lo > _lowRaw ? _volumeByPrice.GetValueOrDefault(lo - _tick.RawNano) : -1;
            long above = hi < _highRaw ? _volumeByPrice.GetValueOrDefault(hi + _tick.RawNano) : -1;
            if (below < 0 && above < 0)
            {
                break;
            }
            if (below >= above) // tie expands toward the lower price
            {
                lo -= _tick.RawNano;
                covered += below;
            }
            else
            {
                hi += _tick.RawNano;
                covered += above;
            }
        }
        return (new Price(hi), new Price(lo));
    }

    /// <summary>True when the bin is a sub-threshold local minimum strictly inside the traded range.</summary>
    public bool IsLvn(Price price)
    {
        long raw = price.RawNano;
        if (MeanPerPriceVolume is not { } mean || raw <= _lowRaw || raw >= _highRaw)
        {
            return false;
        }
        long volume = _volumeByPrice.GetValueOrDefault(raw);
        if (volume >= _opts.LvnMeanRatio * mean)
        {
            return false;
        }
        return NearestUnequal(raw, volume, downward: true) > volume
            && NearestUnequal(raw, volume, downward: false) > volume;
    }

    public IReadOnlyList<Price> Lvns()
    {
        if (_pocRaw == long.MinValue)
        {
            return Array.Empty<Price>();
        }
        var result = new List<Price>();
        for (long raw = _lowRaw + _tick.RawNano; raw < _highRaw; raw += _tick.RawNano)
        {
            if (IsLvn(new Price(raw)))
            {
                result.Add(new Price(raw));
            }
        }
        return result;
    }

    /// <summary>Nearest HVN strictly beyond <paramref name="from"/> in the given direction; null if none.</summary>
    public Price? NextHvn(Price from, bool up)
    {
        if (_pocRaw == long.MinValue)
        {
            return null;
        }
        long step = up ? _tick.RawNano : -_tick.RawNano;
        for (long raw = from.RawNano + step; raw >= _lowRaw && raw <= _highRaw; raw += step)
        {
            if (IsHvn(raw))
            {
                return new Price(raw);
            }
        }
        return null;
    }

    private bool IsHvn(long raw)
    {
        if (MeanPerPriceVolume is not { } mean)
        {
            return false;
        }
        long volume = _volumeByPrice.GetValueOrDefault(raw);
        if (volume < _opts.HvnMeanRatio * mean)
        {
            return false;
        }
        // Local maximum; range edges qualify (volume piling at an extreme is meaningful).
        return NearestUnequal(raw, volume, downward: true) < volume
            && NearestUnequal(raw, volume, downward: false) < volume;
    }

    /// <summary>Volume of the nearest bin with a different volume in one direction;
    /// long.MinValue at the range boundary (counts as "lower" for HVN, never "higher" for LVN).</summary>
    private long NearestUnequal(long raw, long volume, bool downward)
    {
        long step = downward ? -_tick.RawNano : _tick.RawNano;
        for (long p = raw + step; p >= _lowRaw && p <= _highRaw; p += step)
        {
            long v = _volumeByPrice.GetValueOrDefault(p);
            if (v != volume)
            {
                return v;
            }
        }
        return long.MinValue;
    }
}
