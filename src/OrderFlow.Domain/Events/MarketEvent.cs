using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Events;

/// <summary>
/// Normalized market event kind. Maps DBN MBO action chars
/// ('A' Add, 'M' Modify, 'C' Cancel, 'F' Fill, 'T' Trade, 'R' cleaR) plus a synthesized
/// <see cref="SnapshotEnd"/> emitted when the initial book snapshot completes.
/// </summary>
public enum MarketEventKind : byte
{
    None = 0,

    /// <summary>New resting order ('A').</summary>
    AddOrder = 1,

    /// <summary>Order changed price and/or size; Price/Size carry the NEW absolute values ('M').</summary>
    ModifyOrder = 2,

    /// <summary>Order cancelled; Size is the quantity removed ('C').</summary>
    CancelOrder = 3,

    /// <summary>Execution against a resting order; Size is the filled quantity ('F').</summary>
    Fill = 4,

    /// <summary>Aggressor trade summary; does NOT mutate the book — fills do ('T').</summary>
    Trade = 5,

    /// <summary>Clear/reset the entire book ('R').</summary>
    BookClear = 6,

    /// <summary>Synthesized: the snapshot-flagged record run at stream start has ended. Not a DBN record.</summary>
    SnapshotEnd = 7,
}

/// <summary>
/// Record flags, bit-identical to the DBN <c>flags</c> field
/// (databento/dbn rust/dbn/src/flags.rs: LAST=1&lt;&lt;7, TOB=1&lt;&lt;6, SNAPSHOT=1&lt;&lt;5,
/// MBP=1&lt;&lt;4, BAD_TS_RECV=1&lt;&lt;3, MAYBE_BAD_BOOK=1&lt;&lt;2).
/// </summary>
[Flags]
public enum MarketEventFlags : byte
{
    None = 0,
    MaybeBadBook = 1 << 2,
    BadTsRecv = 1 << 3,
    Mbp = 1 << 4,
    Snapshot = 1 << 5,
    TopOfBook = 1 << 6,
    Last = 1 << 7,
}

/// <summary>
/// Single normalized market event. A readonly record struct (not a class hierarchy) so the
/// hot path — tens of millions of events per session — allocates nothing per event.
/// </summary>
public readonly record struct MarketEvent(
    MarketEventKind Kind,
    uint InstrumentId,
    Timestamp TsEvent,
    Timestamp TsRecv,
    uint Sequence,
    ulong OrderId,
    Price Price,
    uint Size,
    Side Side,
    MarketEventFlags Flags)
{
    public bool IsSnapshot => (Flags & MarketEventFlags.Snapshot) != 0;
}
