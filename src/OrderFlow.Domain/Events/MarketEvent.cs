using System.Runtime.CompilerServices;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Events;

/// <summary>
/// Normalized market event kinds derived from MBP-10 records.
/// DBN action chars map as: 'A'/'C'/'M' → <see cref="BookChanged"/> (with the cause
/// preserved in <see cref="BookCause"/>), 'T' → <see cref="Trade"/>, 'R' → <see cref="BookClear"/>.
/// </summary>
public enum MarketEventKind : byte
{
    None = 0,

    /// <summary>Top-10 book state changed; Price/Size/Side/Depth describe the causing update.</summary>
    BookChanged = 1,

    /// <summary>Inline trade; Price/Size are the execution, Side is the AGGRESSOR side.
    /// Levels carry the book state immediately after the trade.</summary>
    Trade = 2,

    /// <summary>Clear/reset the book ('R', stream boundaries).</summary>
    BookClear = 3,

    /// <summary>Synthesized: the snapshot-flagged record run at stream start has ended. Not a DBN record.</summary>
    SnapshotEnd = 4,
}

/// <summary>The order-book action that caused a <see cref="MarketEventKind.BookChanged"/> event.</summary>
public enum BookCause : byte
{
    None = 0,
    Add = 1,    // 'A'
    Cancel = 2, // 'C'
    Modify = 3, // 'M'
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
/// One price level of the MBP-10 state, mirroring the DBN BidAskPair struct
/// (bid_px i64, ask_px i64, bid_sz u32, ask_sz u32, bid_ct u32, ask_ct u32).
/// Unpopulated slots carry <see cref="Price.Undefined"/> prices and zero size/count.
/// </summary>
public readonly record struct BidAskLevel(
    Price BidPrice,
    Price AskPrice,
    uint BidSize,
    uint AskSize,
    uint BidCount,
    uint AskCount)
{
    public static readonly BidAskLevel Empty = new(Price.Undefined, Price.Undefined, 0, 0, 0, 0);

    public bool HasBid => !BidPrice.IsUndefined && BidSize > 0;

    public bool HasAsk => !AskPrice.IsUndefined && AskSize > 0;
}

/// <summary>
/// Fixed 10-level book state as carried by every MBP-10 record. An inline array keeps the
/// whole snapshot a flat value type — no per-event heap allocation through the pipeline.
/// Equality is element-wise (used by tests to assert byte-for-byte state propagation).
/// </summary>
[InlineArray(Depth)]
public struct BookLevels : IEquatable<BookLevels>
{
    public const int Depth = 10;

    private BidAskLevel _element0;

    public static BookLevels CreateEmpty()
    {
        var levels = new BookLevels();
        for (int i = 0; i < Depth; i++)
        {
            levels[i] = BidAskLevel.Empty;
        }
        return levels;
    }

    public readonly bool Equals(BookLevels other)
    {
        for (int i = 0; i < Depth; i++)
        {
            if (this[i] != other[i])
            {
                return false;
            }
        }
        return true;
    }

    public override readonly bool Equals(object? obj) => obj is BookLevels other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        for (int i = 0; i < Depth; i++)
        {
            hash.Add(this[i]);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(BookLevels left, BookLevels right) => left.Equals(right);

    public static bool operator !=(BookLevels left, BookLevels right) => !left.Equals(right);
}

/// <summary>
/// Single normalized market event derived from one MBP-10 record: the causing update
/// (Price/Size/Side/Cause/Depth) plus the resulting top-10 book state (Levels).
/// A readonly record struct so the hot path allocates nothing per event.
/// </summary>
public readonly record struct MarketEvent(
    MarketEventKind Kind,
    BookCause Cause,
    uint InstrumentId,
    Timestamp TsEvent,
    Timestamp TsRecv,
    uint Sequence,
    Price Price,
    uint Size,
    Side Side,
    MarketEventFlags Flags,
    byte Depth,
    BookLevels Levels)
{
    public bool IsSnapshot => (Flags & MarketEventFlags.Snapshot) != 0;
}
