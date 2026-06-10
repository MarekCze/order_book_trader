using System.Threading.Channels;
using OrderFlow.Application.Pipeline;
using OrderFlow.Backtest;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class PipelineTests
{
    private sealed class InMemorySource : IMarketEventSource
    {
        private readonly IReadOnlyList<MarketEvent> _events;

        public InMemorySource(IReadOnlyList<MarketEvent> events)
        {
            _events = events;
        }

        public async Task PumpAsync(ChannelWriter<MarketEvent> writer, CancellationToken cancellationToken = default)
        {
            foreach (var e in _events)
            {
                if (!writer.TryWrite(e))
                {
                    await writer.WriteAsync(e, cancellationToken);
                }
            }
            writer.Complete();
        }
    }

    private sealed class CountingObserver : IBookEventObserver
    {
        public long Count;

        public void OnEventApplied(in MarketEvent e, BookStateTracker tracker) => Count++;
    }

    [Fact]
    public async Task ChannelPipeline_ProducesSameStateAsSynchronousApplication()
    {
        var events = new SyntheticMbp10Generator(seed: 11).Generate(10_000).ToArray();

        var reference = new BookStateTracker();
        foreach (var e in events)
        {
            reference.Apply(in e);
        }

        var stage = new BookStateTrackerStage();
        var observer = new CountingObserver();
        // Tiny channel capacity to force backpressure through the bounded channel path.
        await ReplayPipeline.RunAsync(new InMemorySource(events), stage, observer, channelCapacity: 64);

        Assert.Equal(events.Length, observer.Count);
        Assert.Equal(events.Length, stage.EventsApplied);
        var tracker = Assert.Single(stage.Trackers).Value;

        Assert.Equal(reference.Levels, tracker.Levels); // identical retained 10-level state
        Assert.Equal(reference.HasState, tracker.HasState);
        Assert.Equal(reference.DisplayedTotal(Side.Bid), tracker.DisplayedTotal(Side.Bid));
        Assert.Equal(reference.DisplayedTotal(Side.Ask), tracker.DisplayedTotal(Side.Ask));
    }

    [Fact]
    public async Task Pipeline_RoutesEventsToPerInstrumentTrackers()
    {
        var e1 = new SyntheticMbp10Generator(seed: 12, instrumentId: 1).Generate(500);
        var e2 = new SyntheticMbp10Generator(seed: 12, instrumentId: 2).Generate(500);
        // Deterministic interleave: alternate one event from each instrument.
        var interleaved = e1.Zip(e2, (a, b) => new[] { a, b }).SelectMany(x => x).ToArray();

        var stage = new BookStateTrackerStage();
        await ReplayPipeline.RunAsync(new InMemorySource(interleaved), stage, new CountingObserver());

        Assert.Equal(2, stage.Trackers.Count);
        // Identical seeds → identical per-instrument retained state.
        Assert.Equal(stage.Trackers[1].Levels, stage.Trackers[2].Levels);
        Assert.Equal(stage.Trackers[1].DisplayedTotal(Side.Bid), stage.Trackers[2].DisplayedTotal(Side.Bid));
    }
}
