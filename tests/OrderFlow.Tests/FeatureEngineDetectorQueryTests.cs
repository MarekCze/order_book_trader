using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>
/// M4 query surface: detectors run per event and cannot afford ComputeSnapshot(), so the
/// engine exposes the handful of point queries the setups need (windowed delta + its
/// session percentile, nearest LOI, per-price baseline volume, liquidity tracker,
/// developing POC, ATR percentile, news windows).
/// </summary>
public class FeatureEngineDetectorQueryTests
{
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private static BookLevels Book(decimal bid, uint bidSz, decimal ask, uint askSz) =>
        TestEvents.Levels(
            bids: new (decimal?, uint, uint)[] { (bid, bidSz, 1) },
            asks: new (decimal?, uint, uint)[] { (ask, askSz, 1) });

    private sealed class Harness
    {
        public readonly BookStateTracker Tracker = new();
        public readonly FeatureEngine Engine;

        public Harness(FeatureEngineOptions? options = null) =>
            Engine = new FeatureEngine(TickSize.Es, options ?? new FeatureEngineOptions());

        public void Trade(Side aggressor, decimal px, uint size, ulong ts)
        {
            var e = TestEvents.Trade(aggressor, px, size, Book(5000.00m, 10, 5000.25m, 10), ts);
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
        }
    }

    [Fact]
    public void FlowWindowIndex_ResolvesConfiguredWindows()
    {
        var h = new Harness();
        Assert.Equal(2, h.Engine.FlowWindowIndex(60));
        Assert.Throws<ArgumentException>(() => h.Engine.FlowWindowIndex(45));
    }

    [Fact]
    public void WindowDelta_ReturnsSignedAggressorVolume()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 30, At(0));
        h.Trade(Side.Bid, 5000.25m, 10, At(1));
        Assert.Equal(-20, h.Engine.WindowDelta(h.Engine.FlowWindowIndex(60)));
    }

    [Fact]
    public void DeltaPercentileRank_NullUntilSessionBaselineReady()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 5, At(0));
        Assert.False(h.Engine.DeltaDistributionReady(2));
        Assert.Null(h.Engine.DeltaPercentileRank(2, -5));
    }

    [Fact]
    public void DeltaPercentileRank_AnswersOnceMinSamplesSeen()
    {
        var h = new Harness(new FeatureEngineOptions { SessionMinSamples = 3 });
        // One sample per active second is admitted into the distribution.
        h.Trade(Side.Ask, 5000.00m, 5, At(0));
        h.Trade(Side.Ask, 5000.00m, 5, At(1));
        h.Trade(Side.Ask, 5000.00m, 5, At(2));
        h.Trade(Side.Ask, 5000.00m, 5, At(3));
        Assert.True(h.Engine.DeltaDistributionReady(2));
        var rank = h.Engine.DeltaPercentileRank(2, -1000);
        Assert.NotNull(rank);
        Assert.True(rank <= 0.5);
    }

    [Fact]
    public void TryGetNearestLoi_FindsRoundNumber()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.50m, 1, At(0));
        Assert.True(h.Engine.TryGetNearestLoi(Price.FromDecimal(5000.50m), out var loi));
        Assert.Equal(LoiType.RoundNumber, loi.Type);
        Assert.Equal(Price.FromDecimal(5000.00m), loi.Price);
        Assert.Equal(2, loi.SignedDistanceTicks);
    }

    [Fact]
    public void BaselinePerPriceVolume_MeanOfPriorWindow()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 10, At(0));
        h.Trade(Side.Ask, 5000.25m, 30, At(1));
        Assert.Equal(20.0, h.Engine.BaselinePerPriceVolume!.Value, 9);
    }

    [Fact]
    public void Liquidity_And_DevelopingPoc_AreExposed()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 10, At(0));
        h.Trade(Side.Ask, 5000.25m, 3, At(1));
        Assert.NotNull(h.Engine.Liquidity);
        Assert.Equal(Price.FromDecimal(5000.00m), h.Engine.DevelopingPoc);
    }

    [Fact]
    public void AtrPercentile_NullWithoutHistory()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.00m, 1, At(0));
        Assert.Null(h.Engine.AtrPercentile);
    }

    [Fact]
    public void IsNewsWindow_TrueWithinConfiguredWindow()
    {
        var h = new Harness(new FeatureEngineOptions
        {
            NewsTimesUtc = new[] { "2026-06-01T14:30:00Z" },
        });
        Assert.True(h.Engine.IsNewsWindow(new Timestamp(At(25 * 60))));   // 14:25, inside ±10min
        Assert.False(h.Engine.IsNewsWindow(new Timestamp(At(55 * 60)))); // 14:55, outside
    }
}
