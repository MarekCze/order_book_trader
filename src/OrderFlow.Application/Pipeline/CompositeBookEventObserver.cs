using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>Fans one applied event out to several observers, in fixed order — determinism-safe.</summary>
public sealed class CompositeBookEventObserver : IBookEventObserver
{
    private readonly IBookEventObserver[] _observers;

    public CompositeBookEventObserver(params IBookEventObserver[] observers)
    {
        _observers = observers;
    }

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
    {
        foreach (var observer in _observers)
        {
            observer.OnEventApplied(in e, tracker);
        }
    }
}
