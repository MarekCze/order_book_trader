using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Backtest;

/// <summary>
/// Replay-side collector for <c>orderflow inspect-trade</c>: re-runs the data file through
/// its own feature engine and reconstructs, for one journaled Setup-5 candidate, the
/// detector-internal context that the journal does not carry — the swing-divergence sample
/// that formed the context (H1/H2 and their cumDeltas → E1/E2), the LOI at the extreme
/// (E3), the E4 state at the trigger event, plus a ±window trade tape around the entry.
/// Diagnostic tooling only; nothing here feeds trading logic or the journal.
/// </summary>
public sealed class TradeInspectionCollector : IBookEventObserver
{
    /// <summary>The journaled candidate being reconstructed. HighSide = a short fading a
    /// new high; CenterTsNs is the entry fill ts (or the trigger ts if never filled).</summary>
    public readonly record struct Target(
        long TriggerTsNs, long LevelRaw, bool HighSide, long CenterTsNs, int WindowSeconds);

    private readonly FeatureEngineStage _features;
    private readonly TickSize _tick;
    private readonly Setup5Options _s5;
    private readonly Target _t;
    private readonly long _windowNanos;
    private long _lastTradeRawBeforeTrigger = long.MinValue;

    public TradeInspectionCollector(
        TickSize tick, FeatureEngineOptions featureOptions, Setup5Options setup5, Target target)
    {
        _tick = tick;
        _s5 = setup5;
        _t = target;
        _windowNanos = target.WindowSeconds * 1_000_000_000L;
        _features = new FeatureEngineStage(tick, featureOptions);
    }

    /// <summary>Last divergence sample at/before the trigger whose new extreme equals the
    /// journaled level — the sample that formed the candidate's context.</summary>
    public SwingDivergence? ContextSample { get; private set; }

    /// <summary>Signed LOI distance from the new extreme at context formation (E3); null = none near.</summary>
    public long? ContextLoiDistanceTicks { get; private set; }

    public LoiType? ContextLoiType { get; private set; }

    /// <summary>Forming bar at context formation (E2's bar half): delta and the close's
    /// position toward the extreme (1 = at it).</summary>
    public long ContextBarDelta { get; private set; }

    public double ContextDirectionalRangePos { get; private set; }

    /// <summary>E4 state as of the last event at/before the trigger.</summary>
    public bool E4ImbalanceAtTrigger { get; private set; }

    public bool E4ReclaimedAtTrigger { get; private set; }

    /// <summary>Fresh swing-divergence samples seen over the whole file (per side) — the
    /// "how many new-30-min-extreme events per session" diagnostic.</summary>
    public long DivergenceSamplesHigh { get; private set; }

    public long DivergenceSamplesLow { get; private set; }

    /// <summary>Confirmed swing pivots over the file (SwingPullbackTicks rule).</summary>
    public long ConfirmedSwingHighs { get; private set; }

    public long ConfirmedSwingLows { get; private set; }

    /// <summary>Trades within ±WindowSeconds of the center, in event order.</summary>
    public List<(long TsNs, long PriceRaw, long Size, Side Side)> WindowTrades { get; } = new();

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
    {
        _features.OnEventApplied(in e, tracker);
        if (e.Kind is not (MarketEventKind.BookChanged or MarketEventKind.Trade)
            || CmeSessions.IsMaintenanceBreak(e.TsEvent))
        {
            return; // mirror the engine/detector gating
        }
        var engine = _features.Engines[e.InstrumentId];
        ConfirmedSwingHighs = engine.ConfirmedSwingHighs;
        ConfirmedSwingLows = engine.ConfirmedSwingLows;
        long ts = unchecked((long)e.TsEvent.UnixNanos);

        if (engine.LastHighDivergence is { } hd && hd.Ts == e.TsEvent)
        {
            DivergenceSamplesHigh++;
            if (_t.HighSide && hd.NewExtreme.RawNano == _t.LevelRaw && ts <= _t.TriggerTsNs)
            {
                CaptureContext(engine, hd);
            }
        }
        if (engine.LastLowDivergence is { } ld && ld.Ts == e.TsEvent)
        {
            DivergenceSamplesLow++;
            if (!_t.HighSide && ld.NewExtreme.RawNano == _t.LevelRaw && ts <= _t.TriggerTsNs)
            {
                CaptureContext(engine, ld);
            }
        }

        if (e.Kind == MarketEventKind.Trade)
        {
            if (ts <= _t.TriggerTsNs)
            {
                _lastTradeRawBeforeTrigger = e.Price.RawNano;
            }
            if (Math.Abs(ts - _t.CenterTsNs) <= _windowNanos)
            {
                WindowTrades.Add((ts, e.Price.RawNano, e.Size, e.Side));
            }
        }

        // E4 snapshot: keep refreshing until the trigger event; the final value is the
        // E4 state the detector saw when it armed.
        if (ts <= _t.TriggerTsNs && ContextSample is { } d)
        {
            var level = new Price(_t.LevelRaw);
            E4ImbalanceAtTrigger = _t.HighSide
                ? FootprintFeatures.HasSellImbalanceNear(engine.FormingBar, _tick, level, _s5.ImbalanceProximityTicks, _s5.ImbalanceRatio)
                : FootprintFeatures.HasBuyImbalanceNear(engine.FormingBar, _tick, level, _s5.ImbalanceProximityTicks, _s5.ImbalanceRatio);
            E4ReclaimedAtTrigger = _lastTradeRawBeforeTrigger != long.MinValue
                && (_t.HighSide
                    ? _lastTradeRawBeforeTrigger < d.PriorExtreme.RawNano
                    : _lastTradeRawBeforeTrigger > d.PriorExtreme.RawNano);
        }
    }

    private void CaptureContext(FeatureEngine engine, SwingDivergence sample)
    {
        ContextSample = sample;
        ContextLoiDistanceTicks = engine.TryGetNearestLoi(sample.NewExtreme, out var loi)
            ? loi.SignedDistanceTicks
            : null;
        ContextLoiType = ContextLoiDistanceTicks is null ? null : loi.Type;
        var bar = engine.FormingBar;
        ContextBarDelta = bar.Delta;
        ContextDirectionalRangePos = DirectionalRangePos(bar, _t.HighSide);
    }

    /// <summary>Close position toward the extreme (1 = at it) — same convention as the
    /// detector's E2 evaluation; empty/degenerate bars read as "at the extreme".</summary>
    public static double DirectionalRangePos(FootprintBar bar, bool highSide)
    {
        if (bar.Open.IsUndefined || bar.High == bar.Low)
        {
            return 1.0;
        }
        double span = bar.High.RawNano - bar.Low.RawNano;
        double fromLow = (bar.Close.RawNano - bar.Low.RawNano) / span;
        return highSide ? fromLow : 1.0 - fromLow;
    }
}

/// <summary>±window price/delta path around a reference instant, aggregated into fixed
/// seconds buckets for the inspect-trade printout. Pure function — unit tested.</summary>
public static class PricePathSummary
{
    public readonly record struct Bucket(
        int Index, long Trades, long Volume, long Delta, long LastRaw, long HighRaw, long LowRaw);

    public static List<Bucket> Compute(
        IReadOnlyList<(long TsNs, long PriceRaw, long Size, Side Side)> trades,
        long centerNs, int bucketSeconds, int windowSeconds)
    {
        long bucketNanos = bucketSeconds * 1_000_000_000L;
        int maxIndex = windowSeconds / bucketSeconds;
        var byIndex = new SortedDictionary<int, Bucket>();
        foreach (var (ts, px, size, side) in trades)
        {
            int idx = (int)FloorDiv(ts - centerNs, bucketNanos);
            if (idx < -maxIndex || idx >= maxIndex)
            {
                continue;
            }
            long delta = side == Side.Bid ? size : side == Side.Ask ? -size : 0;
            if (byIndex.TryGetValue(idx, out var b))
            {
                byIndex[idx] = b with
                {
                    Trades = b.Trades + 1,
                    Volume = b.Volume + size,
                    Delta = b.Delta + delta,
                    LastRaw = px,
                    HighRaw = Math.Max(b.HighRaw, px),
                    LowRaw = Math.Min(b.LowRaw, px),
                };
            }
            else
            {
                byIndex[idx] = new Bucket(idx, 1, size, delta, px, px, px);
            }
        }
        return byIndex.Values.ToList();
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : ~(~a / b);
}
