using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>Why a candidate was rejected; None = approved. Journalled verbatim.</summary>
public enum RiskBlock : byte
{
    None = 0,
    Session = 1,           // filter 1: outside RTH / first minutes / news window
    Location = 2,          // filter 2: not within LoiProximityTicks of an LOI
    Volatility = 3,        // filter 3: ATR percentile outside band (or unavailable)
    Spread = 4,            // filter 4: spread ≠ required ticks (or one-sided book)
    PositionOpen = 5,      // filter 5: already exposed (position or working entry)
    AttemptsExhausted = 6, // filter 5: max attempts at this LOI this session
    LevelDead = 7,         // filter 5: consecutive stop-outs killed the level today
    ZeroSize = 8,          // filter 6: risk budget buys zero contracts
}

/// <summary>How an exposure ended, for filter 5 bookkeeping. StopOut covers both stop and
/// invalidation exits at a loss (interpretation: an invalidation IS the level failing).
/// Win = closed net-positive; Other = time stop / scratch / session end; NoFill = the
/// entry never executed (counts as an attempt, doesn't touch the stop-out streak).</summary>
public enum TradeResult : byte
{
    NoFill = 0,
    Win = 1,
    StopOut = 2,
    Other = 3,
}

/// <summary>Everything the global filters need to judge one candidate.
/// <see cref="AtrPercentile"/> is the regime-ATR percentile (30-min ATR vs the trailing
/// distribution), sampled at context formation; <see cref="AtrBaselineSessions"/> is how
/// many distinct sessions that trailing distribution covers — the volatility gate is
/// disabled (pass-through) until it reaches <see cref="RiskOptions.MinBaselineSessions"/>.</summary>
public readonly record struct CandidateContext(
    Timestamp Ts,
    long LevelKey,
    long? LoiDistanceTicks,
    double? AtrPercentile,
    int AtrBaselineSessions,
    bool NewsWindow,
    long? SpreadTicks,
    long StopDistanceTicks);

public readonly record struct RiskVerdict(RiskBlock Block, long Quantity)
{
    public bool Approved => Block == RiskBlock.None;
}

/// <summary>Rulebook global-filter thresholds (appsettings "Risk").</summary>
public sealed class RiskOptions
{
    /// <summary>Filter 1: minutes after the RTH open with no trading (rulebook: 2).</summary>
    public int OpenExcludeMinutes { get; set; } = 2;

    /// <summary>Filter 2: max ticks from a pre-marked LOI (rulebook: 4).</summary>
    public int LoiProximityTicks { get; set; } = 4;

    /// <summary>Filter 3: regime-ATR percentile band vs the trailing distribution (rulebook: [20th, 95th]).</summary>
    public double AtrPercentileMin { get; set; } = 0.20;

    public double AtrPercentileMax { get; set; } = 0.95;

    /// <summary>Filter 3 (redesign): sample the regime ATR at context formation rather than
    /// at the signal trigger, so a volatility burst the setup is reacting to does not push
    /// the reading out of band. False reverts to trigger-time sampling.</summary>
    public bool AtrSampleAtContext { get; set; } = true;

    /// <summary>Filter 3 (redesign): the volatility gate stays disabled (pass-through, logged)
    /// until the trailing regime-ATR baseline covers at least this many distinct sessions.
    /// Below it the percentile is statistically meaningless, so blocking on it is noise.</summary>
    public int MinBaselineSessions { get; set; } = 10;

    /// <summary>Filter 4: required spread in ticks at signal time (rulebook: exactly 1).</summary>
    public int RequiredSpreadTicks { get; set; } = 1;

    /// <summary>Filter 5: max attempts per LOI per session (rulebook: 3).</summary>
    public int MaxAttemptsPerLoi { get; set; } = 3;

    /// <summary>Filter 5: consecutive stop-outs at a level that kill it for the day (rulebook: 2).</summary>
    public int ConsecutiveStopOutsToKillLevel { get; set; } = 2;

    /// <summary>Filter 6: account equity the fixed fraction applies to (backtest constant; no compounding in v1).</summary>
    public decimal AccountEquity { get; set; } = 100_000m;

    /// <summary>Filter 6: risk per trade as a fraction of equity (rulebook: ≤ 0.5%).</summary>
    public double RiskFractionPerTrade { get; set; } = 0.005;

    /// <summary>Filter 6: currency value of one tick per contract (ES: $12.50).</summary>
    public decimal TickValue { get; set; } = 12.50m;

    /// <summary>Engineering safety cap on position size (not from the rulebook).</summary>
    public int MaxPositionContracts { get; set; } = 10;
}

/// <summary>
/// Global filters 1–6, shared by every detector. One instance per traded instrument.
/// Holds the cross-detector exposure state (one position at a time, attempts per LOI,
/// dead levels). Purely event-time driven and deterministic.
/// </summary>
public sealed class RiskManager
{
    private readonly RiskOptions _opts;
    private readonly Dictionary<long, int> _attemptsByLevel = new();
    private readonly Dictionary<long, int> _consecutiveStopOutsByLevel = new();
    private bool _exposed;

    public RiskManager(RiskOptions options) => _opts = options;

    /// <summary>True while an entry order is working or a position is open — filter 5's
    /// "one position at a time" treats a working entry as exposure.</summary>
    public bool Exposed => _exposed;

    /// <summary>How many candidates were approved through a disabled (pass-through) volatility
    /// gate because the regime baseline had &lt; MinBaselineSessions sessions. Diagnostic only
    /// (the "logged" half of the redesign) — surfaced by the backtest CLI.</summary>
    public long VolGatePassThroughCount { get; private set; }

    public RiskVerdict Evaluate(in CandidateContext ctx)
    {
        // 1 — session: RTH only, skip the first minutes after the open and news windows.
        int? rthMinute = TimeContextFeatures.RthMinute(ctx.Ts);
        if (rthMinute is null || rthMinute < _opts.OpenExcludeMinutes || ctx.NewsWindow)
        {
            return new RiskVerdict(RiskBlock.Session, 0);
        }

        // 2 — location: signals in the middle of nowhere are ignored.
        if (ctx.LoiDistanceTicks is not { } dist || Math.Abs(dist) > _opts.LoiProximityTicks)
        {
            return new RiskVerdict(RiskBlock.Location, 0);
        }

        // 3 — volatility regime gate (redesign): use the regime ATR (30-min, sampled at
        // context formation) ranked against the trailing distribution. Until that
        // distribution covers MinBaselineSessions sessions the percentile is meaningless,
        // so the gate is disabled entirely (pass-through, counted). Once established, a
        // dead/overheated regime (percentile outside the band, or unknown) blocks.
        if (ctx.AtrBaselineSessions < _opts.MinBaselineSessions)
        {
            VolGatePassThroughCount++;
        }
        else if (ctx.AtrPercentile is not { } atr || atr < _opts.AtrPercentileMin || atr > _opts.AtrPercentileMax)
        {
            return new RiskVerdict(RiskBlock.Volatility, 0);
        }

        // 4 — spread: a wide spread means the book is stressed.
        if (ctx.SpreadTicks != _opts.RequiredSpreadTicks)
        {
            return new RiskVerdict(RiskBlock.Spread, 0);
        }

        // 5 — exposure.
        if (_exposed)
        {
            return new RiskVerdict(RiskBlock.PositionOpen, 0);
        }
        if (_consecutiveStopOutsByLevel.GetValueOrDefault(ctx.LevelKey) >= _opts.ConsecutiveStopOutsToKillLevel)
        {
            return new RiskVerdict(RiskBlock.LevelDead, 0);
        }
        if (_attemptsByLevel.GetValueOrDefault(ctx.LevelKey) >= _opts.MaxAttemptsPerLoi)
        {
            return new RiskVerdict(RiskBlock.AttemptsExhausted, 0);
        }

        // 6 — sizing: position size = risk budget ÷ (stop distance × tick value).
        long qty = Size(ctx.StopDistanceTicks);
        if (qty <= 0)
        {
            return new RiskVerdict(RiskBlock.ZeroSize, 0);
        }
        return new RiskVerdict(RiskBlock.None, qty);
    }

    /// <summary>Filter 6 sizing for a given stop distance — also used to re-size when an
    /// entry escalates to a wider-stopped order type.</summary>
    public long Size(long stopDistanceTicks)
    {
        decimal budget = _opts.AccountEquity * (decimal)_opts.RiskFractionPerTrade;
        decimal perContract = stopDistanceTicks * _opts.TickValue;
        long qty = perContract > 0 ? (long)decimal.Floor(budget / perContract) : 0;
        return Math.Min(qty, _opts.MaxPositionContracts);
    }

    /// <summary>Claim the single exposure slot and consume one attempt at the level.
    /// Call when an entry order is placed for an approved candidate.</summary>
    public void Reserve(long levelKey)
    {
        _exposed = true;
        _attemptsByLevel[levelKey] = _attemptsByLevel.GetValueOrDefault(levelKey) + 1;
    }

    /// <summary>Free the exposure slot and update the level's stop-out streak.</summary>
    public void Release(long levelKey, TradeResult result)
    {
        _exposed = false;
        switch (result)
        {
            case TradeResult.StopOut:
                _consecutiveStopOutsByLevel[levelKey] =
                    _consecutiveStopOutsByLevel.GetValueOrDefault(levelKey) + 1;
                break;
            case TradeResult.Win:
                _consecutiveStopOutsByLevel.Remove(levelKey);
                break;
        }
    }

    /// <summary>New Globex session: attempts and dead levels are per-day state.</summary>
    public void OnSessionRollover()
    {
        _attemptsByLevel.Clear();
        _consecutiveStopOutsByLevel.Clear();
        _exposed = false;
    }
}
