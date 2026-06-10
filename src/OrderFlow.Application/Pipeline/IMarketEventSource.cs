using System.Threading.Channels;
using OrderFlow.Domain.Events;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Port for anything that produces an ordered stream of market events — a DBN file replay
/// today, a live feed adapter later. Downstream stages never know which one they got.
/// Implementations must write events in stream order and complete the writer when done
/// (with the exception on failure).
/// </summary>
public interface IMarketEventSource
{
    Task PumpAsync(ChannelWriter<MarketEvent> writer, CancellationToken cancellationToken = default);
}
