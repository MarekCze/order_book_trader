using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Trading;

/// <summary>Rulebook setups. Setup 3 (iceberg follow) requires MBO and is deferred in v1;
/// the id is reserved so journals stay stable when it arrives.</summary>
public enum SetupId : byte
{
    AbsorptionFade = 1,
    StopRunFade = 2,
    IcebergFollow = 3,
    LvnVacuum = 4,
    DeltaDivergenceFade = 5,
}

public enum TradeDirection : byte
{
    Long = 1,
    Short = 2,
}

/// <summary>Detector lifecycle (CLAUDE.md): Idle → ContextMet → Armed → OrderWorking →
/// InPosition → Closed. Armed is transient (candidate approved, entry being placed);
/// Closed lasts until the next event resets the machine to Idle.</summary>
public enum SetupState : byte
{
    Idle = 0,
    ContextMet = 1,
    Armed = 2,
    OrderWorking = 3,
    InPosition = 4,
    Closed = 5,
}

/// <summary>Why a trade (or working entry) ended. None = no exit applies (entry expired).</summary>
public enum ExitReason : byte
{
    None = 0,
    Target = 1,
    Stop = 2,
    Invalidation = 3,
    TimeStop = 4,
    SessionEnd = 5,
}

/// <summary>Final disposition of a candidate that was not blocked outright.</summary>
public enum CandidateDisposition : byte
{
    Blocked = 0,
    Expired = 1,
    CancelledInvalidated = 2,
    Traded = 3,
}

/// <summary>
/// One journal row per candidate (detector completed its signal conditions), whether or
/// not it traded. Carries the full F1–F36 snapshot at trigger time — this is the future
/// ML training row; treat the shape as a contract.
/// </summary>
public sealed record CandidateRecord
{
    public required long Id { get; init; }
    public required Timestamp Ts { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required SetupId Setup { get; init; }
    public required TradeDirection Direction { get; init; }
    public required Price Level { get; init; }
    public required RiskBlock Block { get; init; }
    public required long Quantity { get; init; }
    public required FeatureSnapshot Features { get; init; }
}

/// <summary>Terminal outcome for a candidate whose entry order was placed.</summary>
public sealed record CandidateOutcome
{
    public required long CandidateId { get; init; }
    public required CandidateDisposition Disposition { get; init; }
    public required ExitReason ExitReason { get; init; }
    public Fill? EntryFill { get; init; }
    public required IReadOnlyList<Fill> ExitFills { get; init; }
    public required bool T1Filled { get; init; }
    public required long MaeTicks { get; init; }
    public required long MfeTicks { get; init; }
    public required decimal GrossPnl { get; init; }
    public required decimal Commission { get; init; }
    public required decimal NetPnl { get; init; }
}

/// <summary>Journal port (CLAUDE.md: the journal is a first-class output, not logging).
/// Implementations must write deterministically in call order.</summary>
public interface ICandidateJournal
{
    void RecordCandidate(CandidateRecord record);
    void RecordOutcome(CandidateOutcome outcome);
}

public sealed class NullCandidateJournal : ICandidateJournal
{
    public static readonly NullCandidateJournal Instance = new();

    public void RecordCandidate(CandidateRecord record)
    {
    }

    public void RecordOutcome(CandidateOutcome outcome)
    {
    }
}
