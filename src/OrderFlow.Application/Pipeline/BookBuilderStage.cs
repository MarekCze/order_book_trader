using System.Threading.Channels;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Channel consumer that maintains one <see cref="OrderBook"/> per instrument id and
/// forwards each applied event to an observer. Single-threaded consumption keeps the
/// books and everything downstream deterministic.
/// </summary>
public sealed class BookBuilderStage
{
    private readonly Dictionary<uint, OrderBook> _books = new();

    public IReadOnlyDictionary<uint, OrderBook> Books => _books;

    public long EventsApplied { get; private set; }

    public OrderBook GetOrCreateBook(uint instrumentId)
    {
        if (!_books.TryGetValue(instrumentId, out var book))
        {
            book = new OrderBook();
            _books.Add(instrumentId, book);
        }
        return book;
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
                var book = GetOrCreateBook(e.InstrumentId);
                book.Apply(in e);
                EventsApplied++;
                observer.OnEventApplied(in e, book);
            }
        }
    }
}
