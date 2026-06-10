using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>
/// Terse builders for hand-crafted golden-test MBP-10 event sequences. Levels are given
/// as (bidPx, bidSz, bidCt) / (askPx, askSz, askCt) tuples, best first; a null price marks
/// an unpopulated slot.
/// </summary>
internal static class TestEvents
{
    public const uint Instrument = 1;
    private static uint _seq;

    public static BookLevels Levels(
        (decimal? Px, uint Sz, uint Ct)[] bids,
        (decimal? Px, uint Sz, uint Ct)[] asks)
    {
        var levels = BookLevels.CreateEmpty();
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var bid = i < bids.Length ? bids[i] : (Px: null, Sz: 0u, Ct: 0u);
            var ask = i < asks.Length ? asks[i] : (Px: null, Sz: 0u, Ct: 0u);
            levels[i] = new BidAskLevel(
                BidPrice: bid.Px is { } bp ? Price.FromDecimal(bp) : Price.Undefined,
                AskPrice: ask.Px is { } ap ? Price.FromDecimal(ap) : Price.Undefined,
                BidSize: bid.Px is null ? 0 : bid.Sz,
                AskSize: ask.Px is null ? 0 : ask.Sz,
                BidCount: bid.Px is null ? 0 : bid.Ct,
                AskCount: ask.Px is null ? 0 : ask.Ct);
        }
        return levels;
    }

    public static MarketEvent BookChanged(
        BookCause cause, Side side, decimal px, uint size, byte depth, BookLevels levels,
        MarketEventFlags flags = MarketEventFlags.None, ulong ts = 1_000) =>
        new(MarketEventKind.BookChanged, cause, Instrument, new Timestamp(ts), new Timestamp(ts + 1), ++_seq,
            Price.FromDecimal(px), size, side, flags, depth, levels);

    public static MarketEvent Trade(
        Side aggressor, decimal px, uint size, BookLevels levelsAfter, ulong ts = 1_000) =>
        new(MarketEventKind.Trade, BookCause.None, Instrument, new Timestamp(ts), new Timestamp(ts + 1), ++_seq,
            Price.FromDecimal(px), size, aggressor, MarketEventFlags.None, 0, levelsAfter);

    public static MarketEvent Clear(ulong ts = 1_000) =>
        new(MarketEventKind.BookClear, BookCause.None, Instrument, new Timestamp(ts), new Timestamp(ts + 1), ++_seq,
            Price.Undefined, 0, Side.None, MarketEventFlags.None, 0, BookLevels.CreateEmpty());
}
