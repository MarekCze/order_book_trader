using System.Globalization;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Per-instrument feature engine: ingests every applied market event (O(1),
/// allocation-light) and computes an F1–F36 <see cref="FeatureSnapshot"/> on demand.
/// Sessions follow the Globex trading day (rolls 18:00 ET); session distributions then
/// re-seed from the finished session per CLAUDE.md, the finished session's POC enters
/// the naked-POC registry, and its H/L + POC/VAH/VAL become LOIs. Events inside the
/// 17:00–18:00 ET maintenance/pre-open window are never ingested — books there are
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
///  - F8/F10 session distributions are sampled once per active second, not per event.
///  - F16–F18/F21/F22 derive from book-state diffs (see LiquidityDynamicsTracker).
///  - F23–F29 read the FORMING volume bar (leakage rule 2.8.2); F25's percentile ranks
///    against completed bars of the session.
///  - F31: a naked POC is filled when a trade prints at it or the trade-to-trade price
///    path crosses it (gaps between prints still fill).
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

    // M3 components
    private readonly LiquidityDynamicsTracker _liquidity;
    private readonly INakedPocStore _nakedPocs;
    private readonly AtrTracker _atr;
    private readonly SessionLevelsLoiProvider _sessionLevels = new();
    private readonly Timestamp[] _newsTimes;
    private VolumeBarBuilder _bars;
    private SessionProfile _profile;
    private SessionDistribution _barDeltaDist;

    private DateOnly? _sessionDate;
    private Timestamp _lastTs;
    private long _cumDelta;
    private Price _lastTradePrice = Price.Undefined;

    // session level state
    private Price _sessionHigh = Price.Undefined;
    private Price _sessionLow = Price.Undefined;
    private Price _overnightHigh = Price.Undefined;
    private Price _overnightLow = Price.Undefined;
    private bool _rthStarted;
    private Price _priorDayHigh = Price.Undefined;
    private Price _priorDayLow = Price.Undefined;
    private Price _priorPoc = Price.Undefined;
    private Price _priorVah = Price.Undefined;
    private Price _priorVal = Price.Undefined;

    // F9 swing state
    private int _extremeDirection;
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

    public FeatureEngine(
        TickSize tick,
        FeatureEngineOptions options,
        ILoiProvider? loiOverride = null,
        INakedPocStore? nakedPocStore = null,
        IAtrHistoryStore? atrHistoryStore = null)
    {
        _tick = tick;
        _opts = options;
        _nakedPocs = nakedPocStore ?? new InMemoryNakedPocStore();
        _atr = new AtrTracker(options, atrHistoryStore ?? new InMemoryAtrHistoryStore());
        _profile = new SessionProfile(tick, options);
        _bars = new VolumeBarBuilder(tick, options);
        _barDeltaDist = new SessionDistribution(
            -2L * options.BarVolumeSize, 2L * options.BarVolumeSize, options.MinBarSamples);
        // Tie priority: session structure first, then naked POCs, LVNs, round numbers.
        _loi = loiOverride ?? new CompositeLoiProvider(
            _sessionLevels,
            new NakedPocLoiProvider(_nakedPocs),
            new LvnLoiProvider(() => _profile),
            new RoundNumberLoiProvider(options.RoundNumberIntervalPoints));
        _newsTimes = options.NewsTimesUtc
            .Select(s => Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)))
            .ToArray();

        _liquidity = new LiquidityDynamicsTracker(tick, options);
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

    // ----- M4 detector query surface -----
    // Detectors run per event and cannot afford ComputeSnapshot(); these are the O(1)/O(log n)
    // point queries the setups need. All read the same state the snapshot reads.

    /// <summary>Index of a configured flow window by its length in seconds (detector options
    /// reference windows in seconds; the ring is indexed).</summary>
    public int FlowWindowIndex(int seconds)
    {
        int idx = Array.IndexOf(_opts.FlowWindowsSeconds, seconds);
        return idx >= 0
            ? idx
            : throw new ArgumentException(
                $"No configured flow window of {seconds}s (Features:FlowWindowsSeconds = [{string.Join(", ", _opts.FlowWindowsSeconds)}]).");
    }

    /// <summary>F8 raw: signed aggressor volume over the indexed window.</summary>
    public long WindowDelta(int windowIndex) => _ring.WindowSum(windowIndex).Delta;

    /// <summary>True once the windowed-delta session distribution can answer percentile queries
    /// (CLAUDE.md: 200 live samples, or the prior session's final distribution).</summary>
    public bool DeltaDistributionReady(int windowIndex) => _deltaDist[windowIndex].IsReady;

    /// <summary>Fraction of session delta samples ≤ <paramref name="delta"/>; null until ready.</summary>
    public double? DeltaPercentileRank(int windowIndex, long delta) =>
        _deltaDist[windowIndex].PercentileRank(delta);

    /// <summary>F6 source: nearest LOI to a price (A1/B1/C2/D1/E3, global filter 2).</summary>
    public bool TryGetNearestLoi(Price price, out LoiReference loi) =>
        _loi.TryGetNearest(price, _tick, out loi);

    /// <summary>A4 denominator: mean per-price volume over the trailing baseline window (15 min).</summary>
    public double? BaselinePerPriceVolume => _baselineVolume.MeanPerPriceVolume;

    /// <summary>Per-price liquidity queries (A5/A-invalidation: ReplenishRatio, RefreshCount, VanishFlag).</summary>
    public LiquidityDynamicsTracker Liquidity => _liquidity;

    /// <summary>Developing session POC (Setup 1 T2 "nearest opposing structure").</summary>
    public Price? DevelopingPoc => _profile.Poc;

    /// <summary>F34: ATR 5-min percentile vs the trailing day distribution (global filter 3); null without history.</summary>
    public double? AtrPercentile => _sessionDate is { } session ? _atr.Percentile(session) : null;

    /// <summary>F36 source: true within the configured window of a scheduled release (global filter 1).</summary>
    public bool IsNewsWindow(Timestamp ts) =>
        TimeContextFeatures.NewsWindowFlag(ts, _newsTimes, _opts.NewsWindowMinutes);

    public void OnEvent(in MarketEvent e, BookStateTracker tracker)
    {
        _lastTs = e.TsEvent;
        if (e.Kind == MarketEventKind.BookClear)
        {
            _liquidity.Reset();
            return;
        }
        if (e.Kind is not (MarketEventKind.BookChanged or MarketEventKind.Trade))
        {
            return;
        }
        if (CmeSessions.IsMaintenanceBreak(e.TsEvent))
        {
            return;
        }

        RolloverIfNewSession(e.TsEvent);
        if (!_rthStarted && CmeSessions.IsRth(e.TsEvent))
        {
            _rthStarted = true; // overnight H/L freeze here and become LOIs
            RefreshSessionLevels();
        }

        SampleDistributionsAndAdvance(e.TsEvent);
        _stallVolume.Advance(e.TsEvent);
        _baselineVolume.Advance(e.TsEvent);
        _swingMax.Advance(e.TsEvent);
        _swingMin.Advance(e.TsEvent);
        _liquidity.OnEvent(in e, tracker);

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
        if (_swingMax.Extreme is { } hi && priceRaw > hi)
        {
            _extremeDirection = 1;
            _cumDeltaAtExtreme = _cumDelta;
        }
        else if (_swingMin.Extreme is { } lo && priceRaw < lo)
        {
            _extremeDirection = -1;
            _cumDeltaAtExtreme = _cumDelta;
        }
        _swingMax.Add(e.TsEvent, priceRaw);
        _swingMin.Add(e.TsEvent, priceRaw);

        _stallVolume.Add(e.TsEvent, e.Price, size);
        _baselineVolume.Add(e.TsEvent, e.Price, size);

        // ----- M3 per-trade state -----
        _profile.AddTrade(e.Price, size);
        if (_bars.OnTrade(e.Price, e.Size, e.Side) is { } completedBar)
        {
            _barDeltaDist.Add(completedBar.Delta);
        }
        if (_sessionDate is { } session)
        {
            _atr.OnTrade(e.TsEvent, e.Price, session);
        }
        if (_sessionHigh.IsUndefined || e.Price > _sessionHigh)
        {
            _sessionHigh = e.Price;
        }
        if (_sessionLow.IsUndefined || e.Price < _sessionLow)
        {
            _sessionLow = e.Price;
        }
        if (!_rthStarted)
        {
            if (_overnightHigh.IsUndefined || e.Price > _overnightHigh)
            {
                _overnightHigh = e.Price;
            }
            if (_overnightLow.IsUndefined || e.Price < _overnightLow)
            {
                _overnightLow = e.Price;
            }
        }
        FillCrossedNakedPocs(e.Price);

        _lastTradePrice = e.Price;
    }

    /// <summary>F31 fills: any naked POC on the closed price interval between the previous
    /// and current trade prices has been revisited.</summary>
    private void FillCrossedNakedPocs(Price tradePrice)
    {
        var active = _nakedPocs.Active();
        if (active.Count == 0)
        {
            return;
        }
        long lo = tradePrice.RawNano;
        long hi = tradePrice.RawNano;
        if (!_lastTradePrice.IsUndefined)
        {
            lo = Math.Min(lo, _lastTradePrice.RawNano);
            hi = Math.Max(hi, _lastTradePrice.RawNano);
        }
        Span<long> filled = stackalloc long[active.Count < 32 ? active.Count : 32];
        int filledCount = 0;
        foreach (var poc in active)
        {
            if (poc.Price.RawNano >= lo && poc.Price.RawNano <= hi && filledCount < filled.Length)
            {
                filled[filledCount++] = poc.Price.RawNano;
            }
        }
        for (int i = 0; i < filledCount; i++)
        {
            _nakedPocs.MarkFilled(new Price(filled[i]), _sessionDate ?? default);
        }
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
        if (_sessionDate is { } finished)
        {
            for (int i = 0; i < _deltaDist.Length; i++)
            {
                _deltaDist[i] = _deltaDist[i].StartNextSession();
                _intensityDist[i] = _intensityDist[i].StartNextSession();
            }
            _sizeDist = _sizeDist.StartNextSession();
            _barDeltaDist = _barDeltaDist.StartNextSession();
            _cumDelta = 0;
            _extremeDirection = 0;
            _runSide = Side.None;
            _runLength = 0;
            _groupSide = Side.None;
            _groupPriceCount = 0;
            _liquidity.Reset();
            _atr.OnSessionRollover();

            // The finished session's structure becomes context for the new one.
            _priorDayHigh = _sessionHigh;
            _priorDayLow = _sessionLow;
            if (_profile.Poc is { } poc)
            {
                _priorPoc = poc;
                _nakedPocs.Add(finished, poc);
            }
            if (_profile.ValueArea() is { } va)
            {
                _priorVah = va.Vah;
                _priorVal = va.Val;
            }
            _profile = new SessionProfile(_tick, _opts);
            _bars = new VolumeBarBuilder(_tick, _opts);
            _sessionHigh = Price.Undefined;
            _sessionLow = Price.Undefined;
            _overnightHigh = Price.Undefined;
            _overnightLow = Price.Undefined;
            _rthStarted = false;
            RefreshSessionLevels();
            // Time-windowed structures (ring, depth stats, swings, volume windows) flush
            // themselves across the ≥1h close→open gap; no explicit reset needed.
        }
        _sessionDate = date;
    }

    private void RefreshSessionLevels()
    {
        var levels = new List<(Price, LoiType)>(7);
        if (!_priorDayHigh.IsUndefined)
        {
            levels.Add((_priorDayHigh, LoiType.PriorDayHigh));
        }
        if (!_priorDayLow.IsUndefined)
        {
            levels.Add((_priorDayLow, LoiType.PriorDayLow));
        }
        if (!_priorPoc.IsUndefined)
        {
            levels.Add((_priorPoc, LoiType.PriorPoc));
        }
        if (!_priorVah.IsUndefined)
        {
            levels.Add((_priorVah, LoiType.Vah));
        }
        if (!_priorVal.IsUndefined)
        {
            levels.Add((_priorVal, LoiType.Val));
        }
        if (_rthStarted && !_overnightHigh.IsUndefined)
        {
            levels.Add((_overnightHigh, LoiType.OvernightHigh));
            levels.Add((_overnightLow, LoiType.OvernightLow));
        }
        _sessionLevels.SetLevels(levels);
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
        var pullRatioBid = new double?[windowCount];
        var pullRatioAsk = new double?[windowCount];
        for (int i = 0; i < windowCount; i++)
        {
            var agg = _ring.WindowSum(i);
            delta[i] = agg.Delta;
            deltaZ[i] = _deltaDist[i].ZScore(agg.Delta);
            intensity[i] = (double)agg.TradeCount / _opts.FlowWindowsSeconds[i];
            intensityZ[i] = _intensityDist[i].ZScore(agg.TradeCount);
            largePrints[i] = _sizeDist.IsReady ? agg.LargePrintCount : null;
            sweepFlags[i] = agg.SweepCount > 0;
            pullRatioBid[i] = _liquidity.PullRatio(Side.Bid, i);
            pullRatioAsk[i] = _liquidity.PullRatio(Side.Ask, i);
        }

        double? volAtPriceRatio = null;
        bool hasBid = tracker.TryGetBest(Side.Bid, out var bestBid);
        bool hasAsk = tracker.TryGetBest(Side.Ask, out var bestAsk);
        if (hasBid && _baselineVolume.MeanPerPriceVolume is { } mean && mean > 0)
        {
            long stallVol = _stallVolume.VolumeAt(bestBid.Price)
                            + _stallVolume.VolumeAt(bestBid.Price.AddTicks(1, _tick));
            volAtPriceRatio = stallVol / mean;
        }

        var forming = _bars.Forming;
        var (diagBuy, diagSell) = FootprintFeatures.DiagonalImbalanceCounts(forming, _tick, _opts.DiagonalImbalanceRatio);
        var valueArea = _profile.ValueArea();

        long? distNakedPoc = null;
        if (!_lastTradePrice.IsUndefined)
        {
            foreach (var poc in _nakedPocs.Active())
            {
                long dist = _lastTradePrice.TicksFrom(poc.Price, _tick);
                if (distNakedPoc is not { } best || Math.Abs(dist) < Math.Abs(best))
                {
                    distNakedPoc = dist;
                }
            }
        }

        double? lvnDepthRatio = null;
        long? distHvnUp = null;
        long? distHvnDown = null;
        if (!_lastTradePrice.IsUndefined && _profile.MeanPerPriceVolume is { } perPriceMean && perPriceMean > 0)
        {
            lvnDepthRatio = _profile.VolumeAt(_lastTradePrice) / perPriceMean;
            if (_profile.NextHvn(_lastTradePrice, up: true) is { } hvnUp)
            {
                distHvnUp = hvnUp.TicksFrom(_lastTradePrice, _tick);
            }
            if (_profile.NextHvn(_lastTradePrice, up: false) is { } hvnDown)
            {
                distHvnDown = _lastTradePrice.TicksFrom(hvnDown, _tick);
            }
        }

        var (todSin, todCos) = TimeContextFeatures.TodSinCos(_lastTs);

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
            QueuePositionEst = null, // F7: MBO only
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
            PullRatioBid = pullRatioBid,
            PullRatioAsk = pullRatioAsk,
            ReplenishRatioBid = hasBid ? _liquidity.ReplenishRatio(Side.Bid, bestBid.Price, _lastTs) : null,
            ReplenishRatioAsk = hasAsk ? _liquidity.ReplenishRatio(Side.Ask, bestAsk.Price, _lastTs) : null,
            RefreshCountBid = hasBid ? _liquidity.RefreshCount(Side.Bid, bestBid.Price, _lastTs) : 0,
            RefreshCountAsk = hasAsk ? _liquidity.RefreshCount(Side.Ask, bestAsk.Price, _lastTs) : 0,
            RefreshLatencyMs = null, // F19: MBO only
            RefreshSizeCv = null,    // F20: MBO only
            DepthChangeBid = _liquidity.DepthChangeBeyondBest(Side.Bid),
            DepthChangeAsk = _liquidity.DepthChangeBeyondBest(Side.Ask),
            VanishFlagBid = hasBid && _liquidity.VanishFlag(Side.Bid, bestBid.Price, bestBid.DisplayedSize, _lastTs),
            VanishFlagAsk = hasAsk && _liquidity.VanishFlag(Side.Ask, bestAsk.Price, bestAsk.DisplayedSize, _lastTs),
            DiagImbalanceBuyCount = diagBuy,
            DiagImbalanceSellCount = diagSell,
            StackedImbalanceLen = FootprintFeatures.StackedImbalanceLen(forming, _tick, _opts.DiagonalImbalanceRatio),
            BarDelta = forming.Delta,
            BarDeltaPctl = _barDeltaDist.IsReady ? _barDeltaDist.PercentileRank(forming.Delta) : null,
            DeltaPriceDivergence = FootprintFeatures.DeltaPriceDivergence(forming),
            ExtremeVolumeShareHigh = FootprintFeatures.ExtremeVolumeShare(forming, _tick, top: true),
            ExtremeVolumeShareLow = FootprintFeatures.ExtremeVolumeShare(forming, _tick, top: false),
            UnfinishedAuctionHigh = FootprintFeatures.UnfinishedAuction(forming, atHigh: true, _opts.UnfinishedAuctionMinVolume),
            UnfinishedAuctionLow = FootprintFeatures.UnfinishedAuction(forming, atHigh: false, _opts.UnfinishedAuctionMinVolume),
            PocDriftTicks = FootprintFeatures.PocDriftTicks(_bars, _tick),
            DistPocTicks = Dist(_profile.Poc),
            DistVahTicks = Dist(valueArea?.Vah),
            DistValTicks = Dist(valueArea?.Val),
            DistNakedPocTicks = distNakedPoc,
            LvnDepthRatio = lvnDepthRatio,
            DistNextHvnUpTicks = distHvnUp,
            DistNextHvnDownTicks = distHvnDown,
            Atr5Points = _atr.CurrentAtrPoints,
            Atr5Pctl = _sessionDate is { } session ? _atr.Percentile(session) : null,
            TodSin = todSin,
            TodCos = todCos,
            RthMinute = TimeContextFeatures.RthMinute(_lastTs),
            NewsWindowFlag = TimeContextFeatures.NewsWindowFlag(_lastTs, _newsTimes, _opts.NewsWindowMinutes),
        };

        long? Dist(Price? reference) =>
            !_lastTradePrice.IsUndefined && reference is { } r ? _lastTradePrice.TicksFrom(r, _tick) : null;
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
