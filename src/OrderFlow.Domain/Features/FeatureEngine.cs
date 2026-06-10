using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Per-instrument M2 feature engine: ingests every applied market event (O(1),
/// allocation-free in steady state) and computes an F1–F15 <see cref="FeatureSnapshot"/>
/// on demand. Sessions follow the Globex trading day (rolls 18:00 ET); session
/// distributions then re-seed from the finished session per CLAUDE.md. Events inside
/// the 17:00–18:00 ET maintenance/pre-open window are never ingested — books there are
/// legitimately crossed and would poison spread/imbalance state.
///
/// MBP-10 approximations (documented per CLAUDE.md):
///  - F9: "last swing extreme" = a trade printing strictly beyond the rolling 30-minute
///    high/low; cum-delta is recorded including that trade.
///  - F12: prints are classified against the session size distribution as of strictly
///    prior trades (leakage-safe), gated until the distribution is ready.
///  - F13: with no order IDs, a sweep is consecutive T records sharing ts_event and
///    aggressor side across ≥ SweepMinLevels distinct prices.
///  - F14: "current stall" is proxied by a trailing lookback of completed aligned
///    10-second buckets: peak bucket volume ÷ latest completed bucket volume (floor 1).
///  - F8/F10 session distributions are sampled once per active second, not per event,
///    so the 200-sample gate means ~3.5 minutes of active market, not 3 milliseconds.
/// </summary>
public sealed class FeatureEngine
{
    private readonly TickSize _tick;
    private readonly FeatureEngineOptions _opts;
    private readonly ILoiProvider _loi;

    private readonly FlowWindowRing _ring;
    private readonly RollingStats _depthStats;
    private readonly MonotonicWindowExtreme _swingMax;
    private readonly MonotonicWindowExtreme _swingMin;
    private readonly VolumeByPriceWindow _stallVolume;
    private readonly VolumeByPriceWindow _baselineVolume;
    private SessionDistribution[] _deltaDist;
    private SessionDistribution[] _intensityDist;
    private SessionDistribution _sizeDist;

    private DateOnly? _sessionDate;
    private Timestamp _lastTs;
    private long _cumDelta;
    private Price _lastTradePrice = Price.Undefined;

    // F9 swing state
    private int _extremeDirection; // 0 none, +1 high, −1 low
    private long _cumDeltaAtExtreme;

    // F11 run state
    private Side _runSide = Side.None;
    private int _runLength;

    // F13 sweep grouping state
    private Timestamp _groupTs;
    private Side _groupSide = Side.None;
    private readonly long[] _groupPrices = new long[16];
    private int _groupPriceCount;
    private bool _groupCounted;

    public FeatureEngine(TickSize tick, FeatureEngineOptions options, ILoiProvider? loiProvider = null)
    {
        _tick = tick;
        _opts = options;
        _loi = loiProvider ?? new RoundNumberLoiProvider(options.RoundNumberIntervalPoints);
        _ring = new FlowWindowRing(options.FlowWindowsSeconds);
        _depthStats = new RollingStats(options.DepthZWindowSeconds);
        _swingMax = new MonotonicWindowExtreme(options.SwingWindowSeconds, trackMax: true);
        _swingMin = new MonotonicWindowExtreme(options.SwingWindowSeconds, trackMax: false);
        _stallVolume = new VolumeByPriceWindow(options.StallWindowSeconds);
        _baselineVolume = new VolumeByPriceWindow(options.PerPriceBaselineSeconds);
        _deltaDist = new SessionDistribution[options.FlowWindowsSeconds.Length];
        _intensityDist = new SessionDistribution[options.FlowWindowsSeconds.Length];
        for (int i = 0; i < _deltaDist.Length; i++)
        {
            _deltaDist[i] = new SessionDistribution(-options.DeltaHistogramRange, options.DeltaHistogramRange, options.SessionMinSamples);
            _intensityDist[i] = new SessionDistribution(0, options.TradeCountHistogramMax, options.SessionMinSamples);
        }
        _sizeDist = new SessionDistribution(0, options.TradeSizeHistogramMax, options.SessionMinSamples);
    }

    /// <summary>Session cumulative delta (aggressive buys − sells), reset at the Globex roll.</summary>
    public long CumDelta => _cumDelta;

    public void OnEvent(in MarketEvent e, BookStateTracker tracker)
    {
        _lastTs = e.TsEvent;
        if (e.Kind is not (MarketEventKind.BookChanged or MarketEventKind.Trade))
        {
            return;
        }
        if (CmeSessions.IsMaintenanceBreak(e.TsEvent))
        {
            return;
        }

        RolloverIfNewSession(e.TsEvent);
        SampleDistributionsAndAdvance(e.TsEvent);
        _stallVolume.Advance(e.TsEvent);
        _baselineVolume.Advance(e.TsEvent);
        _swingMax.Advance(e.TsEvent);
        _swingMin.Advance(e.TsEvent);

        if (tracker.HasState)
        {
            _depthStats.Add(e.TsEvent, BookShapeFeatures.Top5Depth(tracker.Levels));
        }

        if (e.Kind == MarketEventKind.Trade)
        {
            IngestTrade(in e);
        }
    }

    private void IngestTrade(in MarketEvent e)
    {
        long size = e.Size;

        // F12: classify against strictly prior prints, then admit this one.
        bool isLarge = false;
        if (_sizeDist.IsReady)
        {
            isLarge = size >= _sizeDist.Quantile(_opts.LargePrintPercentile);
        }
        _sizeDist.Add(size);

        // F13: one aggression = consecutive T records sharing ts_event + aggressor side.
        int sweepIncrement = 0;
        if (e.Side != Side.None)
        {
            if (e.TsEvent != _groupTs || e.Side != _groupSide)
            {
                _groupTs = e.TsEvent;
                _groupSide = e.Side;
                _groupPriceCount = 0;
                _groupCounted = false;
            }
            bool newPrice = true;
            for (int i = 0; i < _groupPriceCount; i++)
            {
                if (_groupPrices[i] == e.Price.RawNano)
                {
                    newPrice = false;
                    break;
                }
            }
            if (newPrice && _groupPriceCount < _groupPrices.Length)
            {
                _groupPrices[_groupPriceCount++] = e.Price.RawNano;
            }
            if (!_groupCounted && _groupPriceCount >= _opts.SweepMinLevels)
            {
                _groupCounted = true;
                sweepIncrement = 1;
            }
        }

        _ring.Add(new FlowSample(
            BuyVolume: e.Side == Side.Bid ? size : 0,
            SellVolume: e.Side == Side.Ask ? size : 0,
            TradeCount: 1,
            LargePrintCount: isLarge ? 1 : 0,
            SweepCount: sweepIncrement));

        if (e.Side == Side.Bid)
        {
            _cumDelta += size;
        }
        else if (e.Side == Side.Ask)
        {
            _cumDelta -= size;
        }

        // F11
        if (e.Side != Side.None)
        {
            if (e.Side == _runSide)
            {
                _runLength++;
            }
            else
            {
                _runSide = e.Side;
                _runLength = 1;
            }
        }

        // F9: new extreme = print strictly beyond the rolling 30-minute high/low.
        long priceRaw = e.Price.RawNano;
        long? maxBefore = _swingMax.Extreme;
        long? minBefore = _swingMin.Extreme;
        if (maxBefore is { } hi && priceRaw > hi)
        {
            _extremeDirection = 1;
            _cumDeltaAtExtreme = _cumDelta;
        }
        else if (minBefore is { } lo && priceRaw < lo)
        {
            _extremeDirection = -1;
            _cumDeltaAtExtreme = _cumDelta;
        }
        _swingMax.Add(e.TsEvent, priceRaw);
        _swingMin.Add(e.TsEvent, priceRaw);

        _stallVolume.Add(e.TsEvent, e.Price, size);
        _baselineVolume.Add(e.TsEvent, e.Price, size);
        _lastTradePrice = e.Price;
    }

    /// <summary>
    /// Once per active second (just before the ring rolls into a new one), the windowed
    /// delta and trade count are sampled into the session distributions.
    /// </summary>
    private void SampleDistributionsAndAdvance(Timestamp ts)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        if (_ring.CurrentSecond != long.MinValue && sec > _ring.CurrentSecond)
        {
            for (int i = 0; i < _deltaDist.Length; i++)
            {
                var agg = _ring.WindowSum(i);
                _deltaDist[i].Add(agg.Delta);
                _intensityDist[i].Add(agg.TradeCount);
            }
        }
        _ring.Advance(ts);
    }

    private void RolloverIfNewSession(Timestamp ts)
    {
        var date = CmeSessions.SessionDate(ts);
        if (_sessionDate == date)
        {
            return;
        }
        if (_sessionDate is not null)
        {
            for (int i = 0; i < _deltaDist.Length; i++)
            {
                _deltaDist[i] = _deltaDist[i].StartNextSession();
                _intensityDist[i] = _intensityDist[i].StartNextSession();
            }
            _sizeDist = _sizeDist.StartNextSession();
            _cumDelta = 0;
            _extremeDirection = 0;
            _runSide = Side.None;
            _runLength = 0;
            _groupSide = Side.None;
            _groupPriceCount = 0;
            // Time-windowed structures (ring, depth stats, swings, volume windows) flush
            // themselves across the ≥1h close→open gap; no explicit reset needed.
        }
        _sessionDate = date;
    }

    public FeatureSnapshot ComputeSnapshot(BookStateTracker tracker)
    {
        var levels = tracker.Levels;
        int windowCount = _opts.FlowWindowsSeconds.Length;

        var depthImbalance = new double?[_opts.DepthImbalanceLevels.Length];
        for (int i = 0; i < depthImbalance.Length; i++)
        {
            depthImbalance[i] = BookShapeFeatures.DepthImbalance(levels, _opts.DepthImbalanceLevels[i]);
        }

        long? loiDistance = null;
        LoiType? loiType = null;
        if (!_lastTradePrice.IsUndefined && _loi.TryGetNearest(_lastTradePrice, _tick, out var loi))
        {
            loiDistance = loi.SignedDistanceTicks;
            loiType = loi.Type;
        }

        var delta = new long[windowCount];
        var deltaZ = new double?[windowCount];
        var intensity = new double[windowCount];
        var intensityZ = new double?[windowCount];
        var largePrints = new long?[windowCount];
        var sweepFlags = new bool[windowCount];
        for (int i = 0; i < windowCount; i++)
        {
            var agg = _ring.WindowSum(i);
            delta[i] = agg.Delta;
            deltaZ[i] = _deltaDist[i].ZScore(agg.Delta);
            intensity[i] = (double)agg.TradeCount / _opts.FlowWindowsSeconds[i];
            intensityZ[i] = _intensityDist[i].ZScore(agg.TradeCount);
            largePrints[i] = _sizeDist.IsReady ? agg.LargePrintCount : null;
            sweepFlags[i] = agg.SweepCount > 0;
        }

        double? volAtPriceRatio = null;
        if (tracker.TryGetBest(Side.Bid, out var bestBid) && _baselineVolume.MeanPerPriceVolume is { } mean && mean > 0)
        {
            long stallVol = _stallVolume.VolumeAt(bestBid.Price)
                            + _stallVolume.VolumeAt(bestBid.Price.AddTicks(1, _tick));
            volAtPriceRatio = stallVol / mean;
        }

        return new FeatureSnapshot
        {
            Ts = _lastTs,
            OutOfSession = CmeSessions.IsMaintenanceBreak(_lastTs),
            SpreadTicks = BookShapeFeatures.SpreadTicks(levels, _tick),
            DepthImbalance = depthImbalance,
            BookSlopeBid = BookShapeFeatures.BookSlope(levels, Side.Bid),
            BookSlopeAsk = BookShapeFeatures.BookSlope(levels, Side.Ask),
            BboSizeRatio = BookShapeFeatures.BboSizeRatio(levels),
            DepthZ = tracker.HasState ? _depthStats.ZScore(BookShapeFeatures.Top5Depth(levels)) : null,
            LoiDistanceTicks = loiDistance,
            NearestLoiType = loiType,
            Delta = delta,
            DeltaZ = deltaZ,
            CumDeltaDivergence = _extremeDirection == 0
                ? null
                : (double)(_cumDelta - _cumDeltaAtExtreme) * _extremeDirection,
            TradeIntensity = intensity,
            TradeIntensityZ = intensityZ,
            AggressorRunLength = _runLength,
            AggressorRunSide = _runSide,
            LargePrintCount = largePrints,
            SweepFlag = sweepFlags,
            SellDecay = ComputeDecay(sellSide: true),
            BuyDecay = ComputeDecay(sellSide: false),
            VolAtPriceRatio = volAtPriceRatio,
        };
    }

    /// <summary>
    /// F14: peak completed aligned bucket ÷ latest completed bucket over the lookback
    /// (denominator floored at 1 contract). Null when no completed bucket traded.
    /// </summary>
    private double? ComputeDecay(bool sellSide)
    {
        long currentSec = _ring.CurrentSecond;
        if (currentSec == long.MinValue)
        {
            return null;
        }
        int w = _opts.DecayBucketSeconds;
        long currentBucketStart = currentSec >= 0 ? currentSec / w * w : (currentSec - w + 1) / w * w;
        long peak = 0;
        long latest = -1;
        for (long bs = currentBucketStart - w; bs >= currentBucketStart - _opts.DecayLookbackSeconds; bs -= w)
        {
            var agg = _ring.SumSecondsInclusive(bs, bs + w - 1);
            long vol = sellSide ? agg.SellVolume : agg.BuyVolume;
            if (latest < 0)
            {
                latest = vol;
            }
            peak = Math.Max(peak, vol);
        }
        if (peak == 0)
        {
            return null;
        }
        return (double)peak / Math.Max(latest, 1);
    }
}
