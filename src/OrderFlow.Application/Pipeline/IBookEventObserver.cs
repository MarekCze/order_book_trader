using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Sees every event immediately after it has been applied to its instrument's book state
/// tracker. This is where the feature engine (M2+) and stats collectors plug in.
/// </summary>
public interface IBookEventObserver
{
    void OnEventApplied(in MarketEvent e, BookStateTracker tracker);
}
