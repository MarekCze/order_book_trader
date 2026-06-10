using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

/// <summary>Terse builders for hand-crafted golden-test event sequences.</summary>
internal static class TestEvents
{
    public const uint Instrument = 1;
    private static uint _seq;

    public static MarketEvent Add(ulong id, Side side, decimal px, uint size, MarketEventFlags flags = MarketEventFlags.None, ulong ts = 1_000) =>
        Make(MarketEventKind.AddOrder, id, side, px, size, flags, ts);

    public static MarketEvent Cancel(ulong id, Side side, decimal px, uint size, ulong ts = 1_000) =>
        Make(MarketEventKind.CancelOrder, id, side, px, size, MarketEventFlags.None, ts);

    public static MarketEvent Modify(ulong id, Side side, decimal px, uint size, ulong ts = 1_000) =>
        Make(MarketEventKind.ModifyOrder, id, side, px, size, MarketEventFlags.None, ts);

    public static MarketEvent Fill(ulong id, Side side, decimal px, uint size, ulong ts = 1_000) =>
        Make(MarketEventKind.Fill, id, side, px, size, MarketEventFlags.None, ts);

    public static MarketEvent Trade(Side aggressor, decimal px, uint size, ulong ts = 1_000) =>
        Make(MarketEventKind.Trade, 0, aggressor, px, size, MarketEventFlags.None, ts);

    public static MarketEvent Clear(ulong ts = 1_000) =>
        Make(MarketEventKind.BookClear, 0, Side.None, 0m, 0, MarketEventFlags.None, ts);

    private static MarketEvent Make(MarketEventKind kind, ulong id, Side side, decimal px, uint size, MarketEventFlags flags, ulong ts) =>
        new(kind, Instrument, new Timestamp(ts), new Timestamp(ts + 1), ++_seq, id, Price.FromDecimal(px), size, side, flags);
}
