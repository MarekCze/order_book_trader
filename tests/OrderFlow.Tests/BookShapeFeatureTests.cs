using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class BookShapeFeatureTests
{
    private static readonly TickSize Tick = TickSize.Es;

    private static BookLevels TwoSided() => TestEvents.Levels(
        bids: new (decimal?, uint, uint)[] { (5000.00m, 10, 1), (4999.75m, 20, 2), (4999.50m, 40, 3) },
        asks: new (decimal?, uint, uint)[] { (5000.25m, 5, 1), (5000.50m, 15, 2), (5000.75m, 25, 2) });

    // ----- F1 spread_ticks -----

    [Fact]
    public void SpreadTicks_OneTickBook()
    {
        Assert.Equal(1, BookShapeFeatures.SpreadTicks(TwoSided(), Tick));
    }

    [Fact]
    public void SpreadTicks_OneSidedBook_Null()
    {
        var levels = TestEvents.Levels(
            bids: new (decimal?, uint, uint)[] { (5000.00m, 10, 1) },
            asks: Array.Empty<(decimal?, uint, uint)>());
        Assert.Null(BookShapeFeatures.SpreadTicks(levels, Tick));
    }

    // ----- F2 depth_imbalance_k -----

    [Fact]
    public void DepthImbalance_TopLevel()
    {
        // (10 - 5) / (10 + 5)
        Assert.Equal(1.0 / 3.0, BookShapeFeatures.DepthImbalance(TwoSided(), k: 1)!.Value, precision: 12);
    }

    [Fact]
    public void DepthImbalance_TopThree()
    {
        // bids 70, asks 45 → 25/115
        Assert.Equal(25.0 / 115.0, BookShapeFeatures.DepthImbalance(TwoSided(), k: 3)!.Value, precision: 12);
    }

    [Fact]
    public void DepthImbalance_DeeperKThanBook_UsesWhatExists()
    {
        Assert.Equal(25.0 / 115.0, BookShapeFeatures.DepthImbalance(TwoSided(), k: 10)!.Value, precision: 12);
    }

    [Fact]
    public void DepthImbalance_EmptyBook_Null()
    {
        Assert.Null(BookShapeFeatures.DepthImbalance(BookLevels.CreateEmpty(), k: 5));
    }

    // ----- F3 book_slope -----

    [Fact]
    public void BookSlope_GeometricSizes_IsLogRatio()
    {
        // bid sizes 10, 20, 40: ln(size) = ln10 + i·ln2 → slope ln2 per level
        Assert.Equal(Math.Log(2), BookShapeFeatures.BookSlope(TwoSided(), Side.Bid)!.Value, precision: 10);
    }

    [Fact]
    public void BookSlope_SingleLevel_Null()
    {
        var levels = TestEvents.Levels(
            bids: new (decimal?, uint, uint)[] { (5000.00m, 10, 1) },
            asks: new (decimal?, uint, uint)[] { (5000.25m, 5, 1) });
        Assert.Null(BookShapeFeatures.BookSlope(levels, Side.Bid));
    }

    // ----- F4 bbo_size_ratio -----

    [Fact]
    public void BboSizeRatio_IsLogOfBidOverAsk()
    {
        Assert.Equal(Math.Log(2), BookShapeFeatures.BboSizeRatio(TwoSided())!.Value, precision: 12);
    }

    [Fact]
    public void BboSizeRatio_MissingSide_Null()
    {
        var levels = TestEvents.Levels(
            bids: Array.Empty<(decimal?, uint, uint)>(),
            asks: new (decimal?, uint, uint)[] { (5000.25m, 5, 1) });
        Assert.Null(BookShapeFeatures.BboSizeRatio(levels));
    }

    // ----- F5 helper: top-5 combined depth -----

    [Fact]
    public void Top5Depth_SumsBothSides()
    {
        Assert.Equal(70 + 45, BookShapeFeatures.Top5Depth(TwoSided()));
    }

    // ----- F6 LOI distance (round numbers, the only v1-M2 LOI source) -----

    [Fact]
    public void RoundNumberLoi_NearestBelow_PositiveSignedDistance()
    {
        var provider = new RoundNumberLoiProvider(intervalPoints: 25.00m);
        Assert.True(provider.TryGetNearest(Price.FromDecimal(5010.00m), Tick, out var loi));
        Assert.Equal(LoiType.RoundNumber, loi.Type);
        Assert.Equal(Price.FromDecimal(5000.00m), loi.Price);
        Assert.Equal(40, loi.SignedDistanceTicks); // price 40 ticks above the LOI
    }

    [Fact]
    public void RoundNumberLoi_NearestAbove_NegativeSignedDistance()
    {
        var provider = new RoundNumberLoiProvider(intervalPoints: 25.00m);
        Assert.True(provider.TryGetNearest(Price.FromDecimal(5020.00m), Tick, out var loi));
        Assert.Equal(Price.FromDecimal(5025.00m), loi.Price);
        Assert.Equal(-20, loi.SignedDistanceTicks);
    }

    [Fact]
    public void RoundNumberLoi_ExactlyOnLevel_ZeroDistance()
    {
        var provider = new RoundNumberLoiProvider(intervalPoints: 25.00m);
        Assert.True(provider.TryGetNearest(Price.FromDecimal(5025.00m), Tick, out var loi));
        Assert.Equal(Price.FromDecimal(5025.00m), loi.Price);
        Assert.Equal(0, loi.SignedDistanceTicks);
    }
}
