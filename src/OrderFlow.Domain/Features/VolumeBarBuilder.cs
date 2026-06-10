using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// One footprint bar: OHLC plus per-price volume split by aggressor side. Bars are
/// volume bars (rulebook 2.0 forbids time bars). Side-None trades count into price
/// action and total volume but neither aggressor side.
/// </summary>
public sealed class FootprintBar
{
    private readonly Dictionary<long, (long Buy, long Sell)> _volumeByPrice = new();

    public Price Open { get; private set; } = Price.Undefined;
    public Price Close { get; private set; } = Price.Undefined;
    public Price High { get; private set; } = Price.Undefined;
    public Price Low { get; private set; } = Price.Undefined;
    public long BuyVolume { get; private set; }
    public long SellVolume { get; private set; }
    public long Volume { get; private set; }
    public long Delta => BuyVolume - SellVolume;

    internal void Add(Price price, uint size, Side aggressor)
    {
        if (Open.IsUndefined)
        {
            Open = price;
            High = price;
            Low = price;
        }
        Close = price;
        if (price > High)
        {
            High = price;
        }
        if (price < Low)
        {
            Low = price;
        }
        var (buy, sell) = _volumeByPrice.GetValueOrDefault(price.RawNano);
        if (aggressor == Side.Bid)
        {
            buy += size;
            BuyVolume += size;
        }
        else if (aggressor == Side.Ask)
        {
            sell += size;
            SellVolume += size;
        }
        _volumeByPrice[price.RawNano] = (buy, sell);
        Volume += size;
    }

    public (long Buy, long Sell) VolumeAt(Price price) => _volumeByPrice.GetValueOrDefault(price.RawNano);

    /// <summary>Bar POC: the price with the highest total volume; ties resolve to the lower price.</summary>
    public Price Poc(TickSize tick)
    {
        if (Open.IsUndefined)
        {
            return Price.Undefined;
        }
        // Walk the tick grid Low..High rather than the dictionary — iteration order of
        // the dictionary must never affect results (determinism rule).
        Price best = Price.Undefined;
        long bestVolume = -1;
        for (long raw = Low.RawNano; raw <= High.RawNano; raw += tick.RawNano)
        {
            var (buy, sell) = _volumeByPrice.GetValueOrDefault(raw);
            long total = buy + sell;
            if (total > bestVolume)
            {
                bestVolume = total;
                best = new Price(raw);
            }
        }
        return best;
    }
}

/// <summary>
/// Accumulates trades into volume bars of a configured size. A bar closes on the trade
/// that lifts it to/past the threshold (the closing trade is NOT split, so bars can
/// slightly exceed the size — deterministic and simple). The forming bar is exposed for
/// leakage-safe snapshot features (rulebook 2.8.2); completed bars are kept for F29.
/// </summary>
public sealed class VolumeBarBuilder
{
    private readonly TickSize _tick;
    private readonly FeatureEngineOptions _opts;
    private readonly List<FootprintBar> _completed = new();

    public VolumeBarBuilder(TickSize tick, FeatureEngineOptions options)
    {
        _tick = tick;
        _opts = options;
    }

    public FootprintBar Forming { get; private set; } = new();

    public int CompletedCount { get; private set; }

    /// <summary>F29 span: latest completed bar vs this many bars back (inclusive).</summary>
    public int PocDriftBars => _opts.PocDriftBars;

    /// <summary>Completed bar N back from the latest (0 = most recent); null when out of range.</summary>
    public FootprintBar? CompletedBack(int barsBack) =>
        barsBack >= 0 && barsBack < _completed.Count ? _completed[^(barsBack + 1)] : null;

    /// <summary>Adds a trade; returns the completed bar when this trade closed one.</summary>
    public FootprintBar? OnTrade(Price price, uint size, Side aggressor)
    {
        Forming.Add(price, size, aggressor);
        if (Forming.Volume < _opts.BarVolumeSize)
        {
            return null;
        }
        var done = Forming;
        _completed.Add(done);
        CompletedCount++;
        if (_completed.Count > _opts.PocDriftBars)
        {
            _completed.RemoveAt(0);
        }
        Forming = new FootprintBar();
        return done;
    }
}

/// <summary>
/// Footprint feature calculators (rulebook 2.4) over a single bar. Diagonal convention:
/// a BUY imbalance at price p compares buy(p) against sell(p − 1 tick); a SELL imbalance
/// at p compares sell(p) against buy(p + 1 tick). The opposing cell is floored at one
/// contract, including at bar edges — a heavy one-sided print at an extreme is exactly
/// the trapped-trader signature B5/E4 look for.
/// </summary>
public static class FootprintFeatures
{
    public static (int BuyCount, int SellCount) DiagonalImbalanceCounts(FootprintBar bar, TickSize tick, double ratio)
    {
        int buyCount = 0, sellCount = 0;
        ForEachPrice(bar, tick, (price, buy, sell) =>
        {
            if (IsBuyImbalance(bar, tick, price, buy, ratio))
            {
                buyCount++;
            }
            if (IsSellImbalance(bar, tick, price, sell, ratio))
            {
                sellCount++;
            }
        });
        return (buyCount, sellCount);
    }

    public static int StackedImbalanceLen(FootprintBar bar, TickSize tick, double ratio)
    {
        int maxRun = 0, buyRun = 0, sellRun = 0;
        ForEachPrice(bar, tick, (price, buy, sell) =>
        {
            buyRun = IsBuyImbalance(bar, tick, price, buy, ratio) ? buyRun + 1 : 0;
            sellRun = IsSellImbalance(bar, tick, price, sell, ratio) ? sellRun + 1 : 0;
            maxRun = Math.Max(maxRun, Math.Max(buyRun, sellRun));
        });
        return maxRun;
    }

    /// <summary>F27: share of bar volume in the two tick levels at the high (or low).</summary>
    public static double? ExtremeVolumeShare(FootprintBar bar, TickSize tick, bool top)
    {
        if (bar.Volume == 0)
        {
            return null;
        }
        Price first = top ? bar.High : bar.Low;
        Price second = first.AddTicks(top ? -1 : 1, tick);
        var (b1, s1) = bar.VolumeAt(first);
        var (b2, s2) = bar.VolumeAt(second);
        return (double)(b1 + s1 + b2 + s2) / bar.Volume;
    }

    /// <summary>F28: two-sided volume at the bar extreme — the auction did not finish there.</summary>
    public static bool UnfinishedAuction(FootprintBar bar, bool atHigh, int minVolume)
    {
        if (bar.Volume == 0)
        {
            return false;
        }
        var (buy, sell) = bar.VolumeAt(atHigh ? bar.High : bar.Low);
        return Math.Min(buy, sell) >= minVolume;
    }

    /// <summary>F26: 1 when the bar's price direction and delta disagree.</summary>
    public static bool DeltaPriceDivergence(FootprintBar bar)
    {
        if (bar.Open.IsUndefined)
        {
            return false;
        }
        int priceSign = bar.Close.RawNano.CompareTo(bar.Open.RawNano);
        int deltaSign = bar.Delta.CompareTo(0L);
        return priceSign * deltaSign < 0;
    }

    /// <summary>F29: POC(latest completed bar) − POC(PocDriftBars−1 bars back), in ticks; null until enough bars.</summary>
    public static long? PocDriftTicks(VolumeBarBuilder bars, TickSize tick)
    {
        var latest = bars.CompletedBack(0);
        var reference = bars.CompletedBack(bars.PocDriftBars - 1);
        if (latest is null || reference is null)
        {
            return null;
        }
        return latest.Poc(tick).TicksFrom(reference.Poc(tick), tick);
    }

    private static bool IsBuyImbalance(FootprintBar bar, TickSize tick, Price price, long buy, double ratio)
    {
        var (_, sellBelow) = bar.VolumeAt(price.AddTicks(-1, tick));
        return buy >= ratio * Math.Max(sellBelow, 1);
    }

    private static bool IsSellImbalance(FootprintBar bar, TickSize tick, Price price, long sell, double ratio)
    {
        var (buyAbove, _) = bar.VolumeAt(price.AddTicks(1, tick));
        return sell >= ratio * Math.Max(buyAbove, 1);
    }

    private static void ForEachPrice(FootprintBar bar, TickSize tick, Action<Price, long, long> visit)
    {
        if (bar.Open.IsUndefined)
        {
            return;
        }
        for (long raw = bar.Low.RawNano; raw <= bar.High.RawNano; raw += tick.RawNano)
        {
            var price = new Price(raw);
            var (buy, sell) = bar.VolumeAt(price);
            visit(price, buy, sell);
        }
    }
}
