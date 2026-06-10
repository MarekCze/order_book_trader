using System.Threading.Channels;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Channel consumer that maintains one <see cref="BookStateTracker"/> per instrument id
/// and forwards each applied event to an observer. Single-threaded consumption keeps the
/// trackers and everything downstream deterministic.
/// </summary>
public sealed class BookStateTrackerStage
{
    private readonly Dictionary<uint, BookStateTracker> _trackers = new();

    public IReadOnlyDictionary<uint, BookStateTracker> Trackers => _trackers;

    public long EventsApplied { get; private set; }

    public BookStateTracker GetOrCreateTracker(uint instrumentId)
    {
        if (!_trackers.TryGetValue(instrumentId, out var tracker))
        {
            tracker = new BookStateTracker();
            _trackers.Add(instrumentId, tracker);
        }
        return tracker;
    }

    public async Task RunAsync(
        ChannelReader<MarketEvent> reader,
        IBookEventObserver observer,
        CancellationToken cancellationToken = default)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var e))
            {
                var tracker = GetOrCreateTracker(e.InstrumentId);
                tracker.Apply(in e);
                EventsApplied++;
                observer.OnEventApplied(in e, tracker);
            }
        }
    }
}
