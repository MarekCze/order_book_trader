using OrderFlow.Backtest;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Tests;

/// <summary>
/// inspect-trade tooling: the price-path bucket aggregation (pure) and the replay-side
/// collector reconstructing a Setup-5 context. The collector scenario is the same master
/// short scenario as <see cref="DeltaDivergenceFadeDetectorTests"/>: swing high 4999.50
/// (cumDelta +200) confirmed by the t20 pullback, weak new high H2 = 5000.00 at t65
/// (cumDelta +80), reclaim below the prior swing at t70 (the trigger).
/// </summary>
public class TradeInspectionTests
{
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static ulong At(double seconds) => BaseNanos + (ulong)(seconds * 1_000_000_000);

    private static long AtL(double seconds) => unchecked((long)At(seconds));

    [Fact]
    public void PricePathSummary_BucketsTradesAroundCenter()
    {
        long Ns(double s) => (long)(s * 1_000_000_000);
        long center = Ns(1000);
        var trades = new List<(long TsNs, long PriceRaw, long Size, Side Side)>
        {
            (center + Ns(-5), Price.FromDecimal(4999.75m).RawNano, 10, Side.Bid),
            (center + Ns(-1), Price.FromDecimal(4999.50m).RawNano, 5, Side.Ask),
            (center + Ns(0), Price.FromDecimal(5000.00m).RawNano, 7, Side.Bid),
            (center + Ns(59.9), Price.FromDecimal(5001.00m).RawNano, 3, Side.Ask),
            (center + Ns(60.1), Price.FromDecimal(5002.00m).RawNano, 3, Side.Ask),  // outside
            (center + Ns(-60.1), Price.FromDecimal(5002.00m).RawNano, 3, Side.Ask), // outside
        };

        var buckets = PricePathSummary.Compute(trades, center, bucketSeconds: 10, windowSeconds: 60);

        Assert.Equal(3, buckets.Count);
        var minusOne = buckets[0];
        Assert.Equal(-1, minusOne.Index);
        Assert.Equal(2, minusOne.Trades);
        Assert.Equal(15, minusOne.Volume);
        Assert.Equal(5, minusOne.Delta); // +10 buy − 5 sell
        Assert.Equal(Price.FromDecimal(4999.50m).RawNano, minusOne.LastRaw);
        Assert.Equal(Price.FromDecimal(4999.75m).RawNano, minusOne.HighRaw);
        Assert.Equal(Price.FromDecimal(4999.50m).RawNano, minusOne.LowRaw);
        Assert.Equal(0, buckets[1].Index);
        Assert.Equal(5, buckets[2].Index);
    }

    [Fact]
    public void Collector_ReconstructsSetup5Context_E4_AndWindowTrades()
    {
        var target = new TradeInspectionCollector.Target(
            TriggerTsNs: AtL(70),
            LevelRaw: Price.FromDecimal(5000.00m).RawNano,
            HighSide: true,
            CenterTsNs: AtL(71),
            WindowSeconds: 60);
        var collector = new TradeInspectionCollector(
            TickSize.Es,
            new FeatureEngineOptions { SessionMinSamples = 1, AtrBarSeconds = 60, AtrPeriodBars = 1 },
            new Setup5Options(),
            target);
        var tracker = new BookStateTracker();

        var book = TestEvents.Levels(
            new[] { ((decimal?)4999.75m, 50u, 1u) },
            new[] { ((decimal?)5000.00m, 50u, 1u) });
        void Trade(Side aggressor, decimal px, uint size, double t)
        {
            var e = TestEvents.Trade(aggressor, px, size, book, At(t));
            tracker.Apply(in e);
            collector.OnEventApplied(in e, tracker);
        }

        Trade(Side.Bid, 4999.00m, 50, 0);
        Trade(Side.Bid, 4999.25m, 50, 1);
        Trade(Side.Bid, 4999.50m, 100, 2); // leg peak @ cumDelta +200
        Trade(Side.Ask, 4999.25m, 60, 10);
        Trade(Side.Ask, 4998.50m, 70, 20); // 4-tick pullback confirms the swing high
        Trade(Side.Bid, 5000.00m, 10, 65); // new high H2 on cumDelta +80
        Trade(Side.Ask, 4999.25m, 5, 70);  // reclaim below the prior swing (the trigger)

        Assert.True(collector.ContextSample.HasValue);
        var d = collector.ContextSample!.Value;
        Assert.Equal(Price.FromDecimal(4999.50m), d.PriorExtreme);
        Assert.Equal(Price.FromDecimal(5000.00m), d.NewExtreme);
        Assert.Equal(200, d.CumDeltaPrior);
        Assert.Equal(80, d.CumDeltaNew);

        // E3 at the extreme: 5000.00 is a round-number LOI at distance 0.
        Assert.Equal(0, collector.ContextLoiDistanceTicks);
        Assert.Equal(LoiType.RoundNumber, collector.ContextLoiType);

        // E4 at the t70 trigger: no imbalance required — price reclaimed below H1.
        Assert.True(collector.E4ReclaimedAtTrigger);

        Assert.Equal(1, collector.ConfirmedSwingHighs);
        Assert.Equal(1, collector.DivergenceSamplesHigh);

        // ±60s of the t71 entry: t20, t65 and t70 are inside; t0–t10 are not.
        Assert.Equal(3, collector.WindowTrades.Count);
        Assert.Equal(AtL(20), collector.WindowTrades[0].TsNs);
    }
}
