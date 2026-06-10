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

        public void OnEventApplied(in MarketEvent e, OrderBook book) => Count++;
    }

    [Fact]
    public async Task ChannelPipeline_ProducesSameBookAsSynchronousApplication()
    {
        var events = new SyntheticMboGenerator(seed: 11).Generate(10_000).ToArray();

        var reference = new OrderBook();
        foreach (var e in events)
        {
            reference.Apply(in e);
        }

        var stage = new BookBuilderStage();
        var observer = new CountingObserver();
        // Tiny channel capacity to force backpressure through the bounded channel path.
        await ReplayPipeline.RunAsync(new InMemorySource(events), stage, observer, channelCapacity: 64);

        Assert.Equal(events.Length, observer.Count);
        Assert.Equal(events.Length, stage.EventsApplied);
        var book = Assert.Single(stage.Books).Value;

        Assert.Equal(reference.OrderCount, book.OrderCount);
        Assert.Equal(reference.DisplayedTotal(Side.Bid), book.DisplayedTotal(Side.Bid));
        Assert.Equal(reference.DisplayedTotal(Side.Ask), book.DisplayedTotal(Side.Ask));
        var expected = new PriceLevel[10];
        var actual = new PriceLevel[10];
        foreach (var side in new[] { Side.Bid, Side.Ask })
        {
            int en = reference.CopyTopLevels(side, expected);
            int an = book.CopyTopLevels(side, actual);
            Assert.Equal(en, an);
            Assert.Equal(expected[..en], actual[..an]);
        }
    }

    [Fact]
    public async Task Pipeline_RoutesEventsToPerInstrumentBooks()
    {
        var e1 = new SyntheticMboGenerator(seed: 12, instrumentId: 1).Generate(500);
        var e2 = new SyntheticMboGenerator(seed: 12, instrumentId: 2).Generate(500);
        // Deterministic interleave: alternate one event from each instrument.
        var interleaved = e1.Zip(e2, (a, b) => new[] { a, b }).SelectMany(x => x).ToArray();

        var stage = new BookBuilderStage();
        await ReplayPipeline.RunAsync(new InMemorySource(interleaved), stage, new CountingObserver());

        Assert.Equal(2, stage.Books.Count);
        // Identical seeds → identical per-instrument books.
        Assert.Equal(stage.Books[1].DisplayedTotal(Side.Bid), stage.Books[2].DisplayedTotal(Side.Bid));
        Assert.Equal(stage.Books[1].OrderCount, stage.Books[2].OrderCount);
    }
}
