using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Application.Pipeline;

/// <summary>
/// Pipeline stage owning one <see cref="FeatureEngine"/> per instrument. Runs as an
/// <see cref="IBookEventObserver"/> on the single-threaded consumer side of the channel,
/// so engines stay deterministic. Tracker references are retained per instrument
/// (BookStateTrackerStage keeps them stable) so snapshots can be computed on demand.
/// </summary>
public sealed class FeatureEngineStage : IBookEventObserver
{
    private readonly TickSize _tick;
    private readonly FeatureEngineOptions _options;
    private readonly ILoiProvider? _loiProvider;
    private readonly Dictionary<uint, FeatureEngine> _engines = new();
    private readonly Dictionary<uint, BookStateTracker> _trackers = new();

    public FeatureEngineStage(TickSize tick, FeatureEngineOptions options, ILoiProvider? loiProvider = null)
    {
        _tick = tick;
        _options = options;
        _loiProvider = loiProvider;
    }

    public IReadOnlyDictionary<uint, FeatureEngine> Engines => _engines;

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
    {
        if (!_engines.TryGetValue(e.InstrumentId, out var engine))
        {
            engine = new FeatureEngine(_tick, _options, _loiProvider);
            _engines.Add(e.InstrumentId, engine);
            _trackers.Add(e.InstrumentId, tracker);
        }
        engine.OnEvent(in e, tracker);
    }

    /// <summary>Current F1–F15 snapshot for an instrument; null if it has produced no events.</summary>
    public FeatureSnapshot? Snapshot(uint instrumentId) =>
        _engines.TryGetValue(instrumentId, out var engine)
            ? engine.ComputeSnapshot(_trackers[instrumentId])
            : null;
}
