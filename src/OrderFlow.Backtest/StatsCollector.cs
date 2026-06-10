using OrderFlow.Application.Pipeline;
using OrderFlow.Domain.Book;
using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Backtest;

/// <summary>
/// Replay sanity statistics: event counts by kind, per-instrument session high/low,
/// trade count and traded volume split by aggressor side (trades arrive inline in
/// MBP-10), and the min/max spread observed while the book was two-sided.
/// </summary>
public sealed class StatsCollector : IBookEventObserver
{
    private sealed class InstrumentStats
    {
        public long TradeCount;
        public long Volume;
        public long BuyVolume;   // aggressor = Bid (buyer lifted the offer)
        public long SellVolume;  // aggressor = Ask (seller hit the bid)
        public Price High = Price.Undefined;
        public Price Low = Price.Undefined;
        public long MinSpreadTicks = long.MaxValue;
        public long MaxSpreadTicks = long.MinValue;
        public long TwoSidedSamples;
    }

    private readonly TickSize _tick;
    private readonly long[] _kindCounts = new long[5];
    private readonly Dictionary<uint, InstrumentStats> _instruments = new();

    public StatsCollector(TickSize tick)
    {
        _tick = tick;
    }

    public long TotalEvents { get; private set; }

    public void OnEventApplied(in MarketEvent e, BookStateTracker tracker)
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
            if (e.Side == Side.Bid)
            {
                s.BuyVolume += e.Size;
            }
            else if (e.Side == Side.Ask)
            {
                s.SellVolume += e.Size;
            }
            if (s.High.IsUndefined || e.Price > s.High)
            {
                s.High = e.Price;
            }
            if (s.Low.IsUndefined || e.Price < s.Low)
            {
                s.Low = e.Price;
            }
        }

        if (tracker.TryGetSpreadTicks(_tick, out long spread))
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

    public void Print(TextWriter w, BookStateTrackerStage stage)
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
            w.WriteLine($"  buy volume      {s.BuyVolume,15:N0}  (aggressor bought)");
            w.WriteLine($"  sell volume     {s.SellVolume,15:N0}  (aggressor sold)");
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

            var tracker = stage.Trackers[instrumentId];
            w.Write("  final book      ");
            w.Write($"{tracker.LevelCount(Side.Bid):N0} bid / {tracker.LevelCount(Side.Ask):N0} ask levels");
            if (tracker.TryGetBest(Side.Bid, out var bb) && tracker.TryGetBest(Side.Ask, out var ba))
            {
                w.Write($", BBO {bb.Price} x {ba.Price}");
            }
            w.WriteLine();
        }
    }
}
