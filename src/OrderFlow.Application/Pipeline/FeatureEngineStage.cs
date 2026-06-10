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
    private readonly INakedPocStore? _nakedPocStore;
    private readonly IAtrHistoryStore? _atrHistoryStore;
    private readonly Dictionary<uint, FeatureEngine> _engines = new();
    private readonly Dictionary<uint, BookStateTracker> _trackers = new();

    /// <summary>
    /// The persistence stores are shared by all instruments — fine for v1, which trades a
    /// single continuous ES contract (and a registry that survives the roll is the point
    /// of naked POCs). A multi-instrument bot needs per-instrument registries.
    /// </summary>
    public FeatureEngineStage(
        TickSize tick,
        FeatureEngineOptions options,
        ILoiProvider? loiProvider = null,
        INakedPocStore? nakedPocStore = null,
        IAtrHistoryStore? atrHistoryStore = null)
    {
        _tick = tick;
        _options = options;
        _loiProvider = loiProvider;
        _nakedPocStore = nakedPocStore;
        _atrHistoryStore = atrHistoryStore;
    }

    public IReadOnlyDictionary<uint, FeatureEngine> Engines => _engines;

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
    {
        if (!_engines.TryGetValue(e.InstrumentId, out var engine))
        {
            engine = new FeatureEngine(_tick, _options, _loiProvider, _nakedPocStore, _atrHistoryStore);
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
