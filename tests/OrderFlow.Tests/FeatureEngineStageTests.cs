using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class FeatureEngineStageTests
{
    /// <summary>2026-06-01 14:00:00 UTC — mid-RTH.</summary>
    private static readonly ulong BaseNanos =
        Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero)).UnixNanos;

    private static MarketEvent TradeFor(uint instrument, Side side, decimal px, uint size) =>
        TestEvents.Trade(side, px, size, TestEvents.Levels(
            bids: new (decimal?, uint, uint)[] { (px - 0.25m, 10, 1) },
            asks: new (decimal?, uint, uint)[] { (px + 0.25m, 10, 1) }), ts: BaseNanos)
            with
        { InstrumentId = instrument };

    [Fact]
    public void Stage_MaintainsIndependentPerInstrumentEngines()
    {
        var stage = new FeatureEngineStage(TickSize.Es, new FeatureEngineOptions());
        var trackers = new BookStateTrackerStage();

        var e1 = TradeFor(1, Side.Bid, 5000.25m, 10);
        var t1 = trackers.GetOrCreateTracker(1);
        t1.Apply(in e1);
        stage.OnEventApplied(in e1, t1);

        var e2 = TradeFor(2, Side.Ask, 5000.00m, 4);
        var t2 = trackers.GetOrCreateTracker(2);
        t2.Apply(in e2);
        stage.OnEventApplied(in e2, t2);

        Assert.Equal(2, stage.Engines.Count);
        Assert.Equal(10, stage.Snapshot(1)!.Delta[^1]);
        Assert.Equal(-4, stage.Snapshot(2)!.Delta[^1]);
        Assert.Null(stage.Snapshot(99));
    }

    [Fact]
    public void CompositeObserver_ForwardsToAllInOrder()
    {
        var seen = new List<string>();
        var composite = new CompositeBookEventObserver(
            new RecordingObserver("a", seen),
            new RecordingObserver("b", seen));

        var tracker = new BookStateTracker();
        var e = TradeFor(1, Side.Bid, 5000.25m, 1);
        composite.OnEventApplied(in e, tracker);
        composite.OnEventApplied(in e, tracker);

        Assert.Equal(new[] { "a", "b", "a", "b" }, seen);
    }

    private sealed class RecordingObserver(string name, List<string> log) : IBookEventObserver
    {
        public void OnEventApplied(in MarketEvent e, BookStateTracker tracker) => log.Add(name);
    }
}
