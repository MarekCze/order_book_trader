using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Book;

/// <summary>
/// Top-10 book state tracker for a single instrument (MBP-10 schema). Every MBP-10 record
/// already carries the post-event book state, so this is a state retainer plus query
/// surface — not a book reconstruction engine. There is no order-by-ID tracking at this
/// schema level. Deterministic and allocation-free per event: applying an event is a
/// fixed-size struct copy.
/// </summary>
public sealed class BookStateTracker
{
    private BookLevels _levels = BookLevels.CreateEmpty();

    /// <summary>False until the first state-carrying record arrives (or after a clear).</summary>
    public bool HasState { get; private set; }

    /// <summary>The retained top-10 state — always exactly the level array of the most recent record.</summary>
    public BookLevels Levels => _levels;

    public void Apply(in MarketEvent e)
    {
        switch (e.Kind)
        {
            case MarketEventKind.BookChanged:
            case MarketEventKind.Trade:
                _levels = e.Levels;
                HasState = true;
                break;
            case MarketEventKind.BookClear:
                Clear();
                break;
            case MarketEventKind.SnapshotEnd:
            case MarketEventKind.None:
                break;
        }
    }

    public void Clear()
    {
        _levels = BookLevels.CreateEmpty();
        HasState = false;
    }

    /// <summary>Best level of a side; false when that side is empty (level 0 unpopulated).</summary>
    public bool TryGetBest(Side side, out PriceLevel best)
    {
        var top = _levels[0];
        if (side == Side.Bid && top.HasBid)
        {
            best = new PriceLevel(top.BidPrice, top.BidSize, (int)top.BidCount);
            return true;
        }
        if (side == Side.Ask && top.HasAsk)
        {
            best = new PriceLevel(top.AskPrice, top.AskSize, (int)top.AskCount);
            return true;
        }
        best = default;
        return false;
    }

    /// <summary>Spread in ticks; false when either side is empty.</summary>
    public bool TryGetSpreadTicks(TickSize tick, out long spreadTicks)
    {
        var top = _levels[0];
        if (top.HasBid && top.HasAsk)
        {
            spreadTicks = (top.AskPrice.RawNano - top.BidPrice.RawNano) / tick.RawNano;
            return true;
        }
        spreadTicks = 0;
        return false;
    }

    /// <summary>
    /// Copies up to <paramref name="dest"/>.Length populated levels (best first) into the
    /// span; returns the number written. Populated levels are contiguous from index 0 in a
    /// well-formed feed, but unpopulated slots are skipped defensively regardless.
    /// </summary>
    public int CopyTopLevels(Side side, Span<PriceLevel> dest)
    {
        int written = 0;
        for (int i = 0; i < BookLevels.Depth && written < dest.Length; i++)
        {
            var lvl = _levels[i];
            if (side == Side.Bid)
            {
                if (lvl.HasBid)
                {
                    dest[written++] = new PriceLevel(lvl.BidPrice, lvl.BidSize, (int)lvl.BidCount);
                }
            }
            else if (lvl.HasAsk)
            {
                dest[written++] = new PriceLevel(lvl.AskPrice, lvl.AskSize, (int)lvl.AskCount);
            }
        }
        return written;
    }

    /// <summary>Total displayed size across the visible (top-10) levels of a side.</summary>
    public long DisplayedTotal(Side side)
    {
        long total = 0;
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var lvl = _levels[i];
            total += side == Side.Bid
                ? (lvl.HasBid ? lvl.BidSize : 0)
                : (lvl.HasAsk ? lvl.AskSize : 0);
        }
        return total;
    }

    /// <summary>Displayed size at an exact price within the visible window, 0 if not present.</summary>
    public long DisplayedAt(Side side, Price price)
    {
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var lvl = _levels[i];
            if (side == Side.Bid)
            {
                if (lvl.HasBid && lvl.BidPrice == price)
                {
                    return lvl.BidSize;
                }
            }
            else if (lvl.HasAsk && lvl.AskPrice == price)
            {
                return lvl.AskSize;
            }
        }
        return 0;
    }

    /// <summary>Number of populated levels on a side (≤ 10).</summary>
    public int LevelCount(Side side)
    {
        int count = 0;
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var lvl = _levels[i];
            if (side == Side.Bid ? lvl.HasBid : lvl.HasAsk)
            {
                count++;
            }
        }
        return count;
    }
}
