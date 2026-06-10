namespace OrderFlow.Domain.Book;

/// <summary>
/// Counters for events that reference unknown state (e.g. a stream started mid-session
/// without a snapshot). The book stays self-consistent; these record what was tolerated.
/// </summary>
public sealed class BookAnomalyCounters
{
    public long DuplicateAdd;
    public long CancelUnknownOrder;
    public long FillUnknownOrder;
    public long ModifyUnknownOrder;
    public long SideMismatch;

    public long Total => DuplicateAdd + CancelUnknownOrder + FillUnknownOrder + ModifyUnknownOrder + SideMismatch;
}
