using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>M3 integration: F16-F36 flowing through the FeatureEngine orchestration.</summary>
public class FeatureEngineM3Tests
{
    private const int W300 = 3;

    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private sealed class Harness
    {
        public readonly BookStateTracker Tracker = new();
        public readonly FeatureEngine Engine;
        public readonly InMemoryNakedPocStore NakedPocs = new();

        public Harness(FeatureEngineOptions? options = null) =>
            Engine = new FeatureEngine(TickSize.Es, options ?? new FeatureEngineOptions(), nakedPocStore: NakedPocs);

        public void Trade(Side aggressor, decimal px, uint size, ulong ts,
            (decimal?, uint, uint)[]? bids = null, (decimal?, uint, uint)[]? asks = null)
        {
            var levels = TestEvents.Levels(
                bids ?? new (decimal?, uint, uint)[] { (px - 0.25m, 10, 1) },
                asks ?? new (decimal?, uint, uint)[] { (px + 0.25m, 10, 1) });
            var e = TestEvents.Trade(aggressor, px, size, levels, ts);
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
        }

        public void Book(ulong ts, (decimal?, uint, uint)[] bids, (decimal?, uint, uint)[] asks)
        {
            var e = TestEvents.BookChanged(BookCause.Modify, Side.None, 0.25m, 0, 0,
                TestEvents.Levels(bids, asks), ts: ts);
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
        }

        public FeatureSnapshot Snap() => Engine.ComputeSnapshot(Tracker);
    }

    private static (decimal?, uint, uint)[] L(params (decimal? Px, uint Sz)[] lv) =>
        lv.Select(x => (x.Px, x.Sz, 1u)).ToArray();

    [Fact]
    public void F16_PullRatio_FlowsThroughEngine()
    {
        var h = new Harness();
        h.Book(At(0), L((5000.00m, 10)), L((5000.25m, 10)));
        h.Book(At(1), L((5000.00m, 4)), L((5000.25m, 10)));  // cancel 6
        h.Book(At(2), L((5000.00m, 9)), L((5000.25m, 10)));  // add 5

        Assert.Equal(6.0 / 5.0, h.Snap().PullRatioBid[W300]!.Value, precision: 10);
    }

    [Fact]
    public void F23_F25_FormingBarFeatures()
    {
        var h = new Harness(new FeatureEngineOptions { BarVolumeSize = 1000 });
        h.Trade(Side.Ask, 5000.00m, 5, At(0));
        h.Trade(Side.Bid, 5000.25m, 30, At(1));

        var snap = h.Snap();
        Assert.Equal(25, snap.BarDelta);              // forming bar: +30 −5
        Assert.Equal(1, snap.DiagImbalanceBuyCount);  // buy(5000.25)=30 vs sell(5000.00)=5
        Assert.Null(snap.BarDeltaPctl);               // no completed bars yet
    }

    [Fact]
    public void F30_F32_DistancesToDevelopingProfile()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.00m, 50, At(0));  // POC forms here
        h.Trade(Side.Bid, 5000.50m, 10, At(1));  // last trade 2 ticks above POC

        var snap = h.Snap();
        Assert.Equal(2, snap.DistPocTicks);
        Assert.Equal(10.0 / 30.0, snap.LvnDepthRatio!.Value, precision: 10); // vol@5000.50=10, mean=60/2
    }

    [Fact]
    public void F31_NakedPoc_DistanceAndCrossFill()
    {
        var h = new Harness();
        h.NakedPocs.Add(new DateOnly(2026, 5, 29), Price.FromDecimal(5002.00m));

        h.Trade(Side.Bid, 5000.00m, 1, At(0));
        Assert.Equal(-8, h.Snap().DistNakedPocTicks); // 8 ticks below the naked POC

        h.Trade(Side.Bid, 5003.00m, 1, At(1));        // trades through 5002 → fills it
        Assert.Null(h.Snap().DistNakedPocTicks);
        Assert.Empty(h.NakedPocs.Active());
    }

    [Fact]
    public void Rollover_PriorPocBecomesNakedAndPriorLevelsBecomeLois()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.00m, 50, At(0));   // June 1 session: POC 5000, high 5010
        h.Trade(Side.Bid, 5010.00m, 10, At(1));

        // June 2 session (18:00:01 EDT June 1)
        ulong next = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 22, 0, 1, TimeSpan.Zero)).UnixNanos;
        h.Trade(Side.Bid, 5004.00m, 1, next);

        var snap = h.Snap();
        Assert.Equal(16, snap.DistNakedPocTicks);                // June 1's POC 5000 is now naked; 5004 is 16 ticks above
        // 5000 ties between PriorDayLow/PriorPoc/naked POC/round number — composite
        // priority is session levels first, and PriorDayHigh/Low precede PriorPoc.
        Assert.Equal(LoiType.PriorDayLow, snap.NearestLoiType);
    }

    [Fact]
    public void F35_F36_TimeAndNewsContext()
    {
        var h = new Harness(new FeatureEngineOptions
        {
            NewsTimesUtc = new[] { "2026-06-01T14:00:30Z" },
        });
        h.Trade(Side.Bid, 5000.00m, 1, At(0)); // 14:00 UTC = 10:00 EDT

        var snap = h.Snap();
        Assert.Equal(30, snap.RthMinute);
        Assert.True(snap.NewsWindowFlag);
        Assert.Equal(Math.Sin(2 * Math.PI * 600.0 / 1440.0), snap.TodSin!.Value, precision: 10);
        Assert.Null(snap.Atr5Pctl);            // far from enough ATR history
        Assert.Null(snap.QueuePositionEst);    // F7 forever-null in v1
        Assert.Null(snap.RefreshLatencyMs);    // F19
        Assert.Null(snap.RefreshSizeCv);       // F20
    }
}
