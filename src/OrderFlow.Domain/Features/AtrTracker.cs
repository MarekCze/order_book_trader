using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Domain.Features;

/// <summary>
/// F34's multi-day ATR history (CLAUDE.md: cross-session state persists in SQLite; the
/// adapter lives in Infrastructure). Implementations must return samples in insertion
/// order — deterministic.
/// </summary>
public interface IAtrHistoryStore
{
    void AddSample(DateOnly sessionDate, double atrPoints);

    IReadOnlyList<double> SamplesSince(DateOnly fromInclusive);

    int DistinctDaysSince(DateOnly fromInclusive);
}

public sealed class InMemoryAtrHistoryStore : IAtrHistoryStore
{
    private readonly List<(DateOnly Date, double Atr)> _samples = new();

    public void AddSample(DateOnly sessionDate, double atrPoints) => _samples.Add((sessionDate, atrPoints));

    public IReadOnlyList<double> SamplesSince(DateOnly fromInclusive) =>
        _samples.Where(s => s.Date >= fromInclusive).Select(s => s.Atr).ToArray();

    public int DistinctDaysSince(DateOnly fromInclusive) =>
        _samples.Where(s => s.Date >= fromInclusive).Select(s => s.Date).Distinct().Count();
}

/// <summary>
/// 5-minute-bar ATR (simple moving average of true ranges, F34). Bars align to wall
/// 5-minute boundaries and only exist where trades occurred. Each bar close with a full
/// ATR period also records the ATR into the history store; the percentile ranks the
/// current ATR among all stored samples (today's included) over the trailing lookback,
/// null until enough distinct days exist. The previous close resets at session rollover
/// so the maintenance gap never inflates a true range.
/// </summary>
public sealed class AtrTracker
{
    private readonly FeatureEngineOptions _opts;
    private readonly IAtrHistoryStore _store;
    private readonly Queue<double> _trueRanges = new();
    private long _barIndex = long.MinValue;
    private Price _high = Price.Undefined;
    private Price _low = Price.Undefined;
    private Price _close = Price.Undefined;
    private Price _prevClose = Price.Undefined;

    public AtrTracker(FeatureEngineOptions options, IAtrHistoryStore store)
    {
        _opts = options;
        _store = store;
    }

    public double? CurrentAtrPoints =>
        _trueRanges.Count >= _opts.AtrPeriodBars ? _trueRanges.Average() : null;

    public void OnTrade(Timestamp ts, Price price, DateOnly sessionDate)
    {
        long barIndex = (long)(ts.UnixNanos / 1_000_000_000UL) / _opts.AtrBarSeconds;
        if (barIndex != _barIndex)
        {
            CloseBar(sessionDate);
            _barIndex = barIndex;
            _high = price;
            _low = price;
        }
        if (price > _high)
        {
            _high = price;
        }
        if (price < _low)
        {
            _low = price;
        }
        _close = price;
    }

    public void OnSessionRollover()
    {
        _barIndex = long.MinValue;
        _high = Price.Undefined;
        _low = Price.Undefined;
        _close = Price.Undefined;
        _prevClose = Price.Undefined;
    }

    /// <summary>Percentile rank of the current ATR over the trailing-lookback sample set; null until
    /// the ATR exists and the store covers enough distinct days.</summary>
    public double? Percentile(DateOnly currentSession)
    {
        if (CurrentAtrPoints is not { } atr)
        {
            return null;
        }
        var from = currentSession.AddDays(-_opts.AtrLookbackDays);
        if (_store.DistinctDaysSince(from) < _opts.AtrMinDays)
        {
            return null;
        }
        var samples = _store.SamplesSince(from);
        if (samples.Count == 0)
        {
            return null;
        }
        int atOrBelow = samples.Count(s => s <= atr);
        return (double)atOrBelow / samples.Count;
    }

    private void CloseBar(DateOnly sessionDate)
    {
        if (_barIndex == long.MinValue || _high.IsUndefined)
        {
            return;
        }
        double high = (double)_high.ToDecimal();
        double low = (double)_low.ToDecimal();
        double tr = high - low;
        if (!_prevClose.IsUndefined)
        {
            double prevClose = (double)_prevClose.ToDecimal();
            tr = Math.Max(tr, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
        }
        _trueRanges.Enqueue(tr);
        while (_trueRanges.Count > _opts.AtrPeriodBars)
        {
            _trueRanges.Dequeue();
        }
        _prevClose = _close;
        if (CurrentAtrPoints is { } atr)
        {
            _store.AddSample(sessionDate, atr);
        }
    }
}

/// <summary>F35/F36: time-of-day encodings (Eastern, session-aligned) and the news window flag.</summary>
public static class TimeContextFeatures
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static (double Sin, double Cos) TodSinCos(Timestamp ts)
    {
        var et = TimeZoneInfo.ConvertTime(ts.ToDateTimeOffsetUtc(), Eastern);
        double fraction = et.TimeOfDay.TotalMinutes / 1440.0;
        return (Math.Sin(2 * Math.PI * fraction), Math.Cos(2 * Math.PI * fraction));
    }

    /// <summary>Whole minutes since the 09:30 ET open; null outside RTH.</summary>
    public static int? RthMinute(Timestamp ts)
    {
        if (!CmeSessions.IsRth(ts))
        {
            return null;
        }
        var et = TimeZoneInfo.ConvertTime(ts.ToDateTimeOffsetUtc(), Eastern);
        return (int)(et.TimeOfDay - new TimeSpan(9, 30, 0)).TotalMinutes;
    }

    public static bool NewsWindowFlag(Timestamp ts, IReadOnlyList<Timestamp> newsTimes, int windowMinutes)
    {
        long windowNanos = windowMinutes * 60_000_000_000L;
        for (int i = 0; i < newsTimes.Count; i++)
        {
            long diff = Math.Abs((long)(ts.UnixNanos - newsTimes[i].UnixNanos));
            if (diff <= windowNanos)
            {
                return true;
            }
        }
        return false;
    }
}
