using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Backtest;

/// <summary>
/// Replay sanity statistics: event counts by kind, per-instrument session high/low and
/// volume (from 'T' trade summaries only — fills would double-count), and the min/max
/// spread observed while the book was two-sided.
/// </summary>
public sealed class StatsCollector : IBookEventObserver
{
    private sealed class InstrumentStats
    {
        public long TradeCount;
        public long Volume;
        public Price High = Price.Undefined;
        public Price Low = Price.Undefined;
        public long MinSpreadTicks = long.MaxValue;
        public long MaxSpreadTicks = long.MinValue;
        public long TwoSidedSamples;
    }

    private readonly TickSize _tick;
    private readonly long[] _kindCounts = new long[8];
    private readonly Dictionary<uint, InstrumentStats> _instruments = new();

    public StatsCollector(TickSize tick)
    {
        _tick = tick;
    }

    public long TotalEvents { get; private set; }

    public void OnEventApplied(in MarketEvent e, OrderBook book)
    {
        TotalEvents++;
        _kindCounts[(int)e.Kind]++;

        if (!_instruments.TryGetValue(e.InstrumentId, out var s))
        {
            s = new InstrumentStats();
            _instruments.Add(e.InstrumentId, s);
        }

        if (e.Kind == MarketEventKind.Trade)
        {
            s.TradeCount++;
            s.Volume += e.Size;
            if (s.High.IsUndefined || e.Price > s.High)
            {
                s.High = e.Price;
            }
            if (s.Low.IsUndefined || e.Price < s.Low)
            {
                s.Low = e.Price;
            }
        }

        if (book.TryGetSpreadTicks(_tick, out long spread))
        {
            s.TwoSidedSamples++;
            if (spread < s.MinSpreadTicks)
            {
                s.MinSpreadTicks = spread;
            }
            if (spread > s.MaxSpreadTicks)
            {
                s.MaxSpreadTicks = spread;
            }
        }
    }

    public void Print(TextWriter w, BookBuilderStage stage)
    {
        w.WriteLine("Event counts by kind:");
        for (int i = 0; i < _kindCounts.Length; i++)
        {
            if (_kindCounts[i] > 0)
            {
                w.WriteLine($"  {(MarketEventKind)i,-12} {_kindCounts[i],15:N0}");
            }
        }
        w.WriteLine();

        foreach (var (instrumentId, s) in _instruments.OrderBy(kv => kv.Key))
        {
            w.WriteLine($"Instrument {instrumentId}:");
            w.WriteLine($"  trades          {s.TradeCount,15:N0}");
            w.WriteLine($"  volume          {s.Volume,15:N0}");
            w.WriteLine($"  session high    {(s.High.IsUndefined ? "n/a" : s.High.ToString()),15}");
            w.WriteLine($"  session low     {(s.Low.IsUndefined ? "n/a" : s.Low.ToString()),15}");
            if (s.TwoSidedSamples > 0)
            {
                w.WriteLine($"  spread ticks    min {s.MinSpreadTicks:N0} / max {s.MaxSpreadTicks:N0} ({s.TwoSidedSamples:N0} two-sided samples)");
            }
            else
            {
                w.WriteLine("  spread          book never two-sided");
            }

            var book = stage.Books[instrumentId];
            w.Write("  final book      ");
            w.Write($"{book.OrderCount:N0} orders, {book.LevelCount(Side.Bid):N0} bid / {book.LevelCount(Side.Ask):N0} ask levels");
            if (book.TryGetBest(Side.Bid, out var bb) && book.TryGetBest(Side.Ask, out var ba))
            {
                w.Write($", BBO {bb.Price} x {ba.Price}");
            }
            w.WriteLine();
            if (book.Anomalies.Total > 0)
            {
                w.WriteLine($"  anomalies       dupAdd={book.Anomalies.DuplicateAdd:N0} cxlUnknown={book.Anomalies.CancelUnknownOrder:N0} " +
                            $"fillUnknown={book.Anomalies.FillUnknownOrder:N0} modUnknown={book.Anomalies.ModifyUnknownOrder:N0} sideMismatch={book.Anomalies.SideMismatch:N0}");
            }
        }
    }
}
