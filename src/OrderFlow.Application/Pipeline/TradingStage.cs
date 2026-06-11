using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Trading;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Pipeline stage running the trading side (M4+). Must sit AFTER
/// <see cref="FeatureEngineStage"/> in the composite observer so detectors see features
/// that include the current event. Per event: the event-driven execution adapter (fill
/// simulator) resolves fills for resting orders FIRST — orders react to events strictly
/// older than the ones that fill them — then the coordinator steps the detectors.
/// One coordinator (and execution adapter) per instrument, created lazily; candidate ids
/// come from a single shared sequence so journal rows are unique across instruments.
/// </summary>
public sealed class TradingStage : IBookEventObserver
{
    /// <summary>Builds the per-instrument coordinator and its (optional) event-driven
    /// execution adapter. The host wires the concrete execution (simulator in v1).</summary>
    public delegate (TradingCoordinator Coordinator, IEventDrivenExecution? Execution) CoordinatorFactory(
        FeatureEngine features, Func<long> nextCandidateId);

    private readonly FeatureEngineStage _features;
    private readonly CoordinatorFactory _factory;
    private readonly Dictionary<uint, (TradingCoordinator Coordinator, IEventDrivenExecution? Execution)> _byInstrument = new();
    private long _candidateSeq;

    public TradingStage(FeatureEngineStage features, CoordinatorFactory factory)
    {
        _features = features;
        _factory = factory;
    }

    public IReadOnlyDictionary<uint, (TradingCoordinator Coordinator, IEventDrivenExecution? Execution)> Instruments =>
        _byInstrument;

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
    {
        if (!_byInstrument.TryGetValue(e.InstrumentId, out var entry))
        {
            // The feature stage has already seen this event, so the engine exists.
            entry = _factory(_features.Engines[e.InstrumentId], NextCandidateId);
            _byInstrument.Add(e.InstrumentId, entry);
        }
        entry.Execution?.OnMarketEvent(in e);
        entry.Coordinator.OnEvent(in e, tracker);
    }

    private long NextCandidateId() => ++_candidateSeq;
}
