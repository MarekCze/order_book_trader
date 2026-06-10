using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class FeatureEngineTests
{
    // Default flow windows {10, 30, 60, 300}: index 0 = 10s, 1 = 30s, 2 = 60s, 3 = 300s.
    private const int W10 = 0, W30 = 1, W300 = 3;

    /// <summary>2026-06-01 14:00:00 UTC — 10:00 EDT, mid-RTH, whole minute (10s-aligned).</summary>
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

        public void Trade(Side aggressor, decimal px, uint size, ulong ts, BookLevels? book = null)
        {
            var e = TestEvents.Trade(aggressor, px, size, book ?? Book(5000.00m, 10, 5000.25m, 10), ts);
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
        }

        public void BookEvent(ulong ts, BookLevels? book = null)
        {
            var levels = book ?? Book(5000.00m, 10, 5000.25m, 10);
            var e = TestEvents.BookChanged(BookCause.Modify, Side.Bid, 5000.00m, 10, 0, levels, ts: ts);
            Tracker.Apply(in e);
            Engine.OnEvent(in e, Tracker);
        }

        public FeatureSnapshot Snap() => Engine.ComputeSnapshot(Tracker);
    }

    // ----- F8 delta windows -----

    [Fact]
    public void Delta_PerWindow_EvictsByAge()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.25m, 10, At(0));
        h.Trade(Side.Ask, 5000.00m, 4, At(5));

        var snap = h.Snap();
        Assert.Equal(6, snap.Delta[W10]);
        Assert.Equal(6, snap.Delta[W30]);

        h.BookEvent(At(12)); // buy at t=0 now outside the 10s window
        snap = h.Snap();
        Assert.Equal(-4, snap.Delta[W10]);
        Assert.Equal(6, snap.Delta[W30]);
    }

    // ----- F9 cum-delta divergence -----

    [Fact]
    public void CumDeltaDivergence_NullBeforeAnySwingExtreme()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.00m, 10, At(0));
        Assert.Null(h.Snap().CumDeltaDivergence);
    }

    [Fact]
    public void CumDeltaDivergence_AfterNewHigh_TracksDeltaSinceExtreme()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.00m, 10, At(0));
        h.Trade(Side.Bid, 5000.25m, 5, At(1));  // new 30-min high; cumDelta there = +15
        h.Trade(Side.Ask, 5000.00m, 8, At(2));  // cumDelta now +7

        // (7 − 15) signed by direction (+1 for a high) = −8: sellers since the high
        Assert.Equal(-8.0, h.Snap().CumDeltaDivergence!.Value, precision: 10);
    }

    [Fact]
    public void CumDeltaDivergence_AfterNewLow_SignFlips()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 10, At(0));
        h.Trade(Side.Ask, 4999.75m, 5, At(1));  // new 30-min low; cumDelta there = −15
        h.Trade(Side.Bid, 5000.00m, 8, At(2));  // cumDelta now −7

        // (−7 − (−15)) × (−1) = −8: buyers since the low → divergence against the low
        Assert.Equal(-8.0, h.Snap().CumDeltaDivergence!.Value, precision: 10);
    }

    // ----- F10 trade intensity -----

    [Fact]
    public void TradeIntensity_IsCountPerSecondOverWindow()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.25m, 1, At(0));
        h.Trade(Side.Bid, 5000.25m, 1, At(0.2));
        h.Trade(Side.Ask, 5000.00m, 1, At(0.4));

        Assert.Equal(0.3, h.Snap().TradeIntensity[W10], precision: 10);
    }

    // ----- F11 aggressor run length -----

    [Fact]
    public void AggressorRunLength_ResetsOnSideChange()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.25m, 1, At(0));
        h.Trade(Side.Bid, 5000.25m, 1, At(1));
        Assert.Equal(2, h.Snap().AggressorRunLength);
        Assert.Equal(Side.Bid, h.Snap().AggressorRunSide);

        h.Trade(Side.Ask, 5000.00m, 1, At(2));
        Assert.Equal(1, h.Snap().AggressorRunLength);
        Assert.Equal(Side.Ask, h.Snap().AggressorRunSide);
    }

    // ----- F12 large prints -----

    [Fact]
    public void LargePrintCount_NullUntilSizeDistributionReady()
    {
        var h = new Harness(new FeatureEngineOptions { SessionMinSamples = 10 });
        for (int i = 1; i <= 5; i++)
        {
            h.Trade(Side.Bid, 5000.25m, (uint)i, At(i));
        }
        Assert.Null(h.Snap().LargePrintCount[W300]);
    }

    [Fact]
    public void LargePrintCount_ClassifiesAgainstStrictlyPriorPrints()
    {
        var h = new Harness(new FeatureEngineOptions { SessionMinSamples = 10 });
        for (int i = 1; i <= 10; i++)
        {
            h.Trade(Side.Bid, 5000.25m, (uint)i, At(i)); // sizes 1..10 → q95 = 10
        }
        h.Trade(Side.Bid, 5000.25m, 9, At(11));  // 9 < 10 → not large
        h.Trade(Side.Bid, 5000.25m, 15, At(12)); // 15 ≥ 10 → large

        Assert.Equal(1, h.Snap().LargePrintCount[W300]);
    }

    // ----- F13 sweep flag -----

    [Fact]
    public void SweepFlag_SetWhenOneAggressionConsumesTwoPrices()
    {
        var h = new Harness();
        // same ts_event + same aggressor side at two distinct prices = one sweep
        h.Trade(Side.Ask, 5000.00m, 5, At(1));
        h.Trade(Side.Ask, 4999.75m, 3, At(1));

        Assert.True(h.Snap().SweepFlag[W10]);
        Assert.True(h.Snap().SweepFlag[W300]);
    }

    [Fact]
    public void SweepFlag_NotSetForSinglePriceOrDifferentTimes()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 5, At(1));
        h.Trade(Side.Ask, 5000.00m, 3, At(1));   // same price — not a sweep
        h.Trade(Side.Ask, 4999.75m, 3, At(2));   // different event time — not the same aggression

        Assert.False(h.Snap().SweepFlag[W300]);
    }

    // ----- F14 sell/buy decay -----

    [Fact]
    public void SellDecay_PeakOverLatestCompletedTenSecondBucket()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 100, At(2));  // bucket [0,10): 100
        h.Trade(Side.Ask, 5000.00m, 30, At(12));  // bucket [10,20): 30
        h.BookEvent(At(25));                       // bucket [20,30) is current/partial

        Assert.Equal(100.0 / 30.0, h.Snap().SellDecay!.Value, precision: 10);
        Assert.Null(h.Snap().BuyDecay); // no buy volume at all
    }

    // ----- F15 volume-at-price ratio -----

    [Fact]
    public void VolAtPriceRatio_StallVolumeAtBidOverBaselineMean()
    {
        var h = new Harness();
        h.Trade(Side.Ask, 5000.00m, 40, At(0));
        h.Trade(Side.Ask, 5000.25m, 20, At(10));

        // baseline (15 min): 60 contracts over 2 prices → mean 30
        // stall window (45 s) volume at [bid=5000.00, bid+1=5000.25] = 60 → ratio 2
        Assert.Equal(2.0, h.Snap().VolAtPriceRatio!.Value, precision: 10);
    }

    // ----- F1..F6 snapshot wiring -----

    [Fact]
    public void Snapshot_CarriesBookStateFeatures()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5010.00m, 1, At(0), Book(5010.00m, 10, 5010.25m, 5));

        var snap = h.Snap();
        Assert.Equal(1, snap.SpreadTicks);
        Assert.Equal(1.0 / 3.0, snap.DepthImbalance[0]!.Value, precision: 10);  // k=1
        Assert.Equal(Math.Log(2), snap.BboSizeRatio!.Value, precision: 10);
        Assert.Equal(LoiType.RoundNumber, snap.NearestLoiType);
        Assert.Equal(40, snap.LoiDistanceTicks); // last trade 5010.00 vs round number 5000.00
    }

    [Fact]
    public void DepthZ_AgainstTrailingDistribution()
    {
        var h = new Harness();
        h.BookEvent(At(0), Book(5000.00m, 5, 5000.25m, 5));   // top-5 depth 10
        h.BookEvent(At(1), Book(5000.00m, 10, 5000.25m, 10)); // top-5 depth 20

        // samples {10, 20}: mean 15, sample std √50; latest depth 20 → z = 5/√50
        Assert.Equal(5.0 / Math.Sqrt(50.0), h.Snap().DepthZ!.Value, precision: 10);
    }

    // ----- session handling -----

    [Fact]
    public void MaintenanceBreak_EventsAreNotIngested()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.25m, 10, At(0)); // 14:00 UTC, in-session
        Assert.Equal(10, h.Engine.CumDelta);

        // 21:30 UTC = 17:30 EDT — pre-open, legitimately crossed books, must be skipped
        ulong breakTs = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 21, 30, 0, TimeSpan.Zero)).UnixNanos;
        h.Trade(Side.Bid, 5000.25m, 99, breakTs);

        Assert.Equal(10, h.Engine.CumDelta);
        Assert.True(h.Snap().OutOfSession);
    }

    [Fact]
    public void SessionRollover_ResetsCumulativeState()
    {
        var h = new Harness();
        h.Trade(Side.Bid, 5000.25m, 10, At(0)); // June 1 session
        Assert.Equal(10, h.Engine.CumDelta);

        // 22:00:01 UTC = 18:00:01 EDT — June 2 session has opened
        ulong nextSession = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 22, 0, 1, TimeSpan.Zero)).UnixNanos;
        h.Trade(Side.Ask, 5000.00m, 3, nextSession);

        Assert.Equal(-3, h.Engine.CumDelta);
    }
}
