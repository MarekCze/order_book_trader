using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Domain.Trading;

/// <summary>Detector configuration root (appsettings "Detectors"). Setups 2/4/5 arrive in
/// M5; Setup 3 is deferred until MBO data exists.</summary>
public sealed class TradingOptions
{
    public Setup1Options Setup1 { get; set; } = new();
}

/// <summary>
/// Per-instrument trading orchestrator: owns the detectors (a long and a short instance
/// per enabled setup, stepped in fixed order — determinism), the shared
/// <see cref="RiskManager"/> (one position at a time across ALL detectors), session
/// rollover of per-day risk state, and fill routing. Detectors place orders through a
/// wrapping port that records ownership, so fills coming back from the execution layer
/// reach the detector that placed the order — including fills emitted synchronously
/// inside Place (market orders).
/// </summary>
public sealed class TradingCoordinator : IFillListener
{
    private readonly IExecutionPort _exec;
    private readonly FeatureEngine _features;
    private readonly RiskManager _risk;
    private readonly SetupDetectorBase[] _detectors;
    private readonly Dictionary<long, int> _orderOwner = new();
    private int _placingOwner = -1;
    private DateOnly? _session;

    public TradingCoordinator(
        TickSize tick,
        TradingOptions options,
        RiskOptions riskOptions,
        ExecutionOptions execOptions,
        IExecutionPort exec,
        ICandidateJournal journal,
        FeatureEngine features,
        Func<long> nextCandidateId)
    {
        _exec = exec;
        _features = features;
        _risk = new RiskManager(riskOptions);
        var detectors = new List<SetupDetectorBase>();
        if (options.Setup1.Enabled)
        {
            foreach (var direction in new[] { TradeDirection.Long, TradeDirection.Short })
            {
                detectors.Add(new AbsorptionFadeDetector(
                    tick, options.Setup1, direction, _risk,
                    new RoutedPort(this, detectors.Count), journal, execOptions, riskOptions, nextCandidateId));
            }
        }
        _detectors = detectors.ToArray();
    }

    public IReadOnlyList<SetupDetectorBase> Detectors => _detectors;

    public void OnEvent(in MarketEvent e, BookStateTracker tracker)
    {
        if (e.Kind is not (MarketEventKind.BookChanged or MarketEventKind.Trade))
        {
            return;
        }
        var date = CmeSessions.SessionDate(e.TsEvent);
        if (_session != date)
        {
            if (_session is not null)
            {
                _risk.OnSessionRollover(); // attempts and dead levels are per-day state
            }
            _session = date;
        }
        foreach (var detector in _detectors)
        {
            detector.OnEvent(in e, tracker, _features);
        }
    }

    public void OnFill(in Fill fill)
    {
        if (_orderOwner.Remove(fill.OrderId, out int owner))
        {
            _detectors[owner].OnFill(in fill, _features);
        }
        else if (_placingOwner >= 0)
        {
            // Synchronous fill emitted inside Place, before ownership was registered.
            _detectors[_placingOwner].OnFill(in fill, _features);
        }
    }

    /// <summary>Execution port wrapper recording which detector owns each order id.</summary>
    private sealed class RoutedPort : IExecutionPort
    {
        private readonly TradingCoordinator _owner;
        private readonly int _detectorIndex;

        public RoutedPort(TradingCoordinator owner, int detectorIndex)
        {
            _owner = owner;
            _detectorIndex = detectorIndex;
        }

        public long Place(in OrderSpec spec)
        {
            _owner._placingOwner = _detectorIndex;
            try
            {
                long id = _owner._exec.Place(in spec);
                _owner._orderOwner[id] = _detectorIndex;
                return id;
            }
            finally
            {
                _owner._placingOwner = -1;
            }
        }

        public bool Cancel(long orderId)
        {
            _owner._orderOwner.Remove(orderId);
            return _owner._exec.Cancel(orderId);
        }
    }
}
