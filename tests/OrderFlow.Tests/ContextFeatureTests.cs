using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class AtrTrackerTests
{
    private static Price Px(decimal p) => Price.FromDecimal(p);

    /// <summary>2026-06-01 14:00:00 UTC — aligned to a 5-minute boundary.</summary>
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static Timestamp At(double seconds) => new(BaseNanos + (ulong)(seconds * 1_000_000_000));

    private static readonly DateOnly Session = new(2026, 6, 1);

    [Fact]
    public void Atr_IsMeanTrueRangeOverPeriod()
    {
        var store = new InMemoryAtrHistoryStore();
        var atr = new AtrTracker(new FeatureEngineOptions { AtrPeriodBars = 2, AtrMinDays = 1 }, store);

        // bar A [0, 300): H 5002, L 5000, C 5001 → TR (no prior close) = 2 points
        atr.OnTrade(At(0), Px(5000.00m), Session);
        atr.OnTrade(At(60), Px(5002.00m), Session);
        atr.OnTrade(At(120), Px(5001.00m), Session);
        // bar B [300, 600): H 5004, L 5001, C 5003 → TR = max(3, |5004−5001|, |5001−5001|) = 3
        atr.OnTrade(At(301), Px(5001.00m), Session);
        atr.OnTrade(At(400), Px(5004.00m), Session);
        atr.OnTrade(At(500), Px(5003.00m), Session);
        Assert.Null(atr.CurrentAtrPoints); // bar B not yet closed → only one completed TR
        // first trade of bar C closes bar B
        atr.OnTrade(At(601), Px(5003.00m), Session);

        Assert.Equal(2.5, atr.CurrentAtrPoints!.Value, precision: 10);
    }

    [Fact]
    public void AtrPercentile_RankAmongTrailingSamples_GatedByDays()
    {
        var store = new InMemoryAtrHistoryStore();
        store.AddSample(new DateOnly(2026, 5, 28), 1.0);
        store.AddSample(new DateOnly(2026, 5, 29), 2.0);
        store.AddSample(new DateOnly(2026, 5, 30), 3.0);
        store.AddSample(new DateOnly(2026, 5, 31), 4.0);

        var atr = new AtrTracker(new FeatureEngineOptions { AtrPeriodBars = 2, AtrMinDays = 2, AtrLookbackDays = 20 }, store);
        // produce ATR 2.5 as above (also stored as today's sample)
        atr.OnTrade(At(0), Px(5000.00m), Session);
        atr.OnTrade(At(60), Px(5002.00m), Session);
        atr.OnTrade(At(301), Px(5001.00m), Session);
        atr.OnTrade(At(400), Px(5004.00m), Session);
        atr.OnTrade(At(500), Px(5003.00m), Session);
        atr.OnTrade(At(601), Px(5003.00m), Session);

        // samples in window: 1, 2, 3, 4, 2.5 → rank of 2.5 = 3/5
        Assert.Equal(0.6, atr.Percentile(Session)!.Value, precision: 10);
    }

    [Fact]
    public void AtrPercentile_NullBeforeEnoughDays()
    {
        var store = new InMemoryAtrHistoryStore();
        var atr = new AtrTracker(new FeatureEngineOptions { AtrPeriodBars = 2, AtrMinDays = 5 }, store);
        atr.OnTrade(At(0), Px(5000.00m), Session);

        Assert.Null(atr.Percentile(Session));
    }
}

public class TimeContextFeatureTests
{
    private static Timestamp Utc(int h, int mi) =>
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, h, mi, 0, TimeSpan.Zero));

    [Fact]
    public void TodSinCos_QuarterDayEastern()
    {
        // 10:00 UTC = 06:00 EDT = minute 360 of 1440 → angle π/2
        var (sin, cos) = TimeContextFeatures.TodSinCos(Utc(10, 0));
        Assert.Equal(1.0, sin, precision: 10);
        Assert.Equal(0.0, cos, precision: 10);
    }

    [Fact]
    public void RthMinute_MinutesSinceOpen_NullOutside()
    {
        Assert.Equal(30, TimeContextFeatures.RthMinute(Utc(14, 0)));  // 10:00 EDT
        Assert.Equal(0, TimeContextFeatures.RthMinute(Utc(13, 30)));  // 09:30 EDT
        Assert.Null(TimeContextFeatures.RthMinute(Utc(13, 29)));
        Assert.Null(TimeContextFeatures.RthMinute(Utc(21, 30)));
    }

    [Fact]
    public void NewsWindowFlag_WithinConfiguredMinutes()
    {
        var news = new[] { Utc(14, 30) };
        Assert.True(TimeContextFeatures.NewsWindowFlag(Utc(14, 21), news, windowMinutes: 10));
        Assert.True(TimeContextFeatures.NewsWindowFlag(Utc(14, 40), news, windowMinutes: 10));
        Assert.False(TimeContextFeatures.NewsWindowFlag(Utc(14, 41), news, windowMinutes: 10));
        Assert.False(TimeContextFeatures.NewsWindowFlag(Utc(14, 19), news, windowMinutes: 10));
    }
}
