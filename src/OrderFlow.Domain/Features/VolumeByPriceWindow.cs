using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Traded volume per price over a trailing time window (F15's stall-window volume and
/// 15-minute per-price baseline). A queue of raw trades drives eviction; a dictionary
/// keyed by raw price holds current per-price totals. An entry ages out when exactly
/// windowSeconds old. Deterministic: only point lookups and counts are exposed —
/// iteration order of the dictionary never affects results.
/// </summary>
public sealed class VolumeByPriceWindow
{
    private readonly int _windowSeconds;
    private readonly Queue<(long Second, long PriceRaw, long Size)> _trades = new();
    private readonly Dictionary<long, long> _volumeByPrice = new();
    private long _totalVolume;

    public VolumeByPriceWindow(int windowSeconds)
    {
        _windowSeconds = windowSeconds;
    }

    public long TotalVolume => _totalVolume;

    public int DistinctPriceCount => _volumeByPrice.Count;

    /// <summary>Window volume ÷ distinct prices traded — the "per-price mean" of A4/F15.</summary>
    public double? MeanPerPriceVolume =>
        _volumeByPrice.Count > 0 ? (double)_totalVolume / _volumeByPrice.Count : null;

    public void Add(Timestamp ts, Price price, long size)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        Evict(sec);
        _trades.Enqueue((sec, price.RawNano, size));
        _volumeByPrice[price.RawNano] = _volumeByPrice.GetValueOrDefault(price.RawNano) + size;
        _totalVolume += size;
    }

    /// <summary>Evicts aged trades without adding (call before reading after idle gaps).</summary>
    public void Advance(Timestamp ts) => Evict((long)(ts.UnixNanos / 1_000_000_000UL));

    public long VolumeAt(Price price) => _volumeByPrice.GetValueOrDefault(price.RawNano);

    private void Evict(long nowSecond)
    {
        while (_trades.Count > 0 && nowSecond - _trades.Peek().Second >= _windowSeconds)
        {
            var (_, priceRaw, size) = _trades.Dequeue();
            long remaining = _volumeByPrice[priceRaw] - size;
            if (remaining == 0)
            {
                _volumeByPrice.Remove(priceRaw);
            }
            else
            {
                _volumeByPrice[priceRaw] = remaining;
            }
            _totalVolume -= size;
        }
    }
}
