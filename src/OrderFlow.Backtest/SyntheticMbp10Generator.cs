using OrderFlow.Domain.Events;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Backtest;

/// <summary>
/// Deterministic (seeded) synthetic MBP-10 stream: a random-walk top-10 book where every
/// event carries both its cause and the resulting level state, exactly like the real
/// schema. Liquidity adds/cancels/modifies, best-price improvements, and aggressor trades
/// that consume levels (revealing a deeper level in the same record, as real MBP-10 feeds
/// do). Guaranteed never to produce a crossed book.
/// Test/benchmark infrastructure only — the production pipeline forbids randomness; this
/// seeded generator is itself fully deterministic for a given (seed, count).
/// </summary>
public sealed class SyntheticMbp10Generator
{
    private struct LevelState
    {
        public long PriceRaw;
        public uint Size;
        public uint Count;
    }

    private readonly Random _rng;
    private readonly TickSize _tick = TickSize.Es;
    private readonly uint _instrumentId;
    private readonly long _anchorPrice;
    private readonly bool _seedFlaggedAsSnapshot;

    private readonly List<LevelState> _bids = new(BookLevels.Depth); // descending price
    private readonly List<LevelState> _asks = new(BookLevels.Depth); // ascending price

    private uint _sequence;
    private ulong _ts;

    public SyntheticMbp10Generator(
        int seed = 42,
        uint instrumentId = 1,
        decimal startPrice = 5000.00m,
        ulong startTsNs = 1_767_621_600_000_000_000UL, // 2026-01-05 14:00:00 UTC
        bool seedFlaggedAsSnapshot = false)
    {
        _rng = new Random(seed);
        _instrumentId = instrumentId;
        _anchorPrice = Price.FromDecimal(startPrice).RawNano;
        _ts = startTsNs;
        _seedFlaggedAsSnapshot = seedFlaggedAsSnapshot;
    }

    /// <summary>Number of seed records emitted before random-walk operations begin.</summary>
    public const int SeedRecordCount = 2 * BookLevels.Depth;

    public IEnumerable<MarketEvent> Generate(long count)
    {
        long produced = 0;

        // Seed 10 levels per side, one Add record each, book building up progressively.
        var seedFlags = _seedFlaggedAsSnapshot ? MarketEventFlags.Snapshot : MarketEventFlags.None;
        for (int i = 0; i < BookLevels.Depth && produced < count; i++)
        {
            var bid = new LevelState
            {
                PriceRaw = _anchorPrice - (i + 1) * _tick.RawNano,
                Size = (uint)(50 + _rng.Next(450)),
                Count = (uint)(1 + _rng.Next(20)),
            };
            _bids.Add(bid);
            yield return Next(MarketEventKind.BookChanged, BookCause.Add, bid.PriceRaw, bid.Size, Side.Bid, (byte)i, seedFlags);
            produced++;
            if (produced >= count)
            {
                break;
            }
            var ask = new LevelState
            {
                PriceRaw = _anchorPrice + i * _tick.RawNano,
                Size = (uint)(50 + _rng.Next(450)),
                Count = (uint)(1 + _rng.Next(20)),
            };
            _asks.Add(ask);
            yield return Next(MarketEventKind.BookChanged, BookCause.Add, ask.PriceRaw, ask.Size, Side.Ask, (byte)i, seedFlags);
            produced++;
        }

        while (produced < count)
        {
            int roll = _rng.Next(100);
            if (roll < 30)
            {
                yield return AddLiquidity();
                produced++;
            }
            else if (roll < 50)
            {
                yield return CancelLiquidity();
                produced++;
            }
            else if (roll < 65)
            {
                yield return ModifyLevel();
                produced++;
            }
            else if (roll < 85)
            {
                yield return TryImproveBest() ?? AddLiquidity();
                produced++;
            }
            else
            {
                foreach (var e in TradeBurst())
                {
                    if (produced >= count)
                    {
                        break;
                    }
                    yield return e;
                    produced++;
                }
            }
        }
    }

    private MarketEvent AddLiquidity()
    {
        var (side, levels) = PickSide();
        int idx = _rng.Next(levels.Count);
        var lvl = levels[idx];
        uint delta = (uint)(1 + _rng.Next(100));
        lvl.Size += delta;
        lvl.Count += 1;
        levels[idx] = lvl;
        return Next(MarketEventKind.BookChanged, BookCause.Add, lvl.PriceRaw, delta, side, (byte)idx, MarketEventFlags.None);
    }

    private MarketEvent CancelLiquidity()
    {
        var (side, levels) = PickSide();
        int idx = _rng.Next(levels.Count);
        var lvl = levels[idx];
        uint amount = (uint)(1 + _rng.Next((int)lvl.Size));
        long price = lvl.PriceRaw;
        if (amount >= lvl.Size)
        {
            amount = lvl.Size;
            RemoveAndReplenish(levels, idx, side);
        }
        else
        {
            lvl.Size -= amount;
            lvl.Count = Math.Max(1, lvl.Count - 1);
            levels[idx] = lvl;
        }
        return Next(MarketEventKind.BookChanged, BookCause.Cancel, price, amount, side, (byte)idx, MarketEventFlags.None);
    }

    private MarketEvent ModifyLevel()
    {
        var (side, levels) = PickSide();
        int idx = _rng.Next(levels.Count);
        var lvl = levels[idx];
        lvl.Size = (uint)(1 + _rng.Next(500));
        levels[idx] = lvl;
        return Next(MarketEventKind.BookChanged, BookCause.Modify, lvl.PriceRaw, lvl.Size, side, (byte)idx, MarketEventFlags.None);
    }

    private MarketEvent? TryImproveBest()
    {
        if (_bids.Count == 0 || _asks.Count == 0)
        {
            return null;
        }
        long spreadTicks = (_asks[0].PriceRaw - _bids[0].PriceRaw) / _tick.RawNano;
        if (spreadTicks <= 1)
        {
            return null; // already 1 tick wide — cannot improve without crossing
        }
        // Step up to 5 ticks into the spread: strong mean reversion, so the random walk's
        // spread stays realistic instead of drifting wide as trades consume best levels.
        long step = 1 + _rng.Next((int)Math.Min(5, spreadTicks - 1));
        var side = _rng.Next(2) == 0 ? Side.Bid : Side.Ask;
        var levels = side == Side.Bid ? _bids : _asks;
        var fresh = new LevelState
        {
            PriceRaw = side == Side.Bid
                ? _bids[0].PriceRaw + step * _tick.RawNano
                : _asks[0].PriceRaw - step * _tick.RawNano,
            Size = (uint)(1 + _rng.Next(200)),
            Count = (uint)(1 + _rng.Next(5)),
        };
        levels.Insert(0, fresh);
        if (levels.Count > BookLevels.Depth)
        {
            levels.RemoveAt(levels.Count - 1); // pushed below the visible window
        }
        return Next(MarketEventKind.BookChanged, BookCause.Add, fresh.PriceRaw, fresh.Size, side, 0, MarketEventFlags.None);
    }

    /// <summary>
    /// Aggressor trade: consumes the opposite best level (possibly sweeping into the
    /// second), one Trade record per price consumed, each carrying the post-trade state —
    /// removal of a level reveals a replenished worst level in the same record, as the
    /// real top-10 window does.
    /// </summary>
    private IEnumerable<MarketEvent> TradeBurst()
    {
        Side aggressor = _rng.Next(2) == 0 ? Side.Bid : Side.Ask;
        var resting = aggressor == Side.Bid ? _asks : _bids;
        Side restingSide = aggressor.Opposite();
        uint remaining = (uint)(1 + _rng.Next(600));
        int levelsSwept = 0;
        while (remaining > 0 && levelsSwept < 2 && resting.Count > 0)
        {
            var best = resting[0];
            uint qty = Math.Min(remaining, best.Size);
            if (qty >= best.Size)
            {
                RemoveAndReplenish(resting, 0, restingSide);
            }
            else
            {
                best.Size -= qty;
                resting[0] = best;
            }
            remaining -= qty;
            levelsSwept++;
            yield return Next(MarketEventKind.Trade, BookCause.None, best.PriceRaw, qty, aggressor, 0, MarketEventFlags.None);
        }
    }

    private void RemoveAndReplenish(List<LevelState> levels, int idx, Side side)
    {
        levels.RemoveAt(idx);
        if (levels.Count == 0)
        {
            // Degenerate: rebuild one level a tick away from the anchor so the walk continues.
            levels.Add(new LevelState
            {
                PriceRaw = side == Side.Bid ? _anchorPrice - _tick.RawNano : _anchorPrice + _tick.RawNano,
                Size = (uint)(50 + _rng.Next(450)),
                Count = (uint)(1 + _rng.Next(20)),
            });
            return;
        }
        long worst = levels[^1].PriceRaw;
        levels.Add(new LevelState
        {
            PriceRaw = side == Side.Bid ? worst - _tick.RawNano : worst + _tick.RawNano,
            Size = (uint)(50 + _rng.Next(450)),
            Count = (uint)(1 + _rng.Next(20)),
        });
    }

    private (Side Side, List<LevelState> Levels) PickSide() =>
        _rng.Next(2) == 0 ? (Side.Bid, _bids) : (Side.Ask, _asks);

    private MarketEvent Next(MarketEventKind kind, BookCause cause, long priceRaw, uint size, Side side, byte depth, MarketEventFlags flags)
    {
        _ts += (ulong)(1 + _rng.Next(1000)) * 1_000; // 1µs–1ms between events
        _sequence++;
        return new MarketEvent(kind, cause, _instrumentId, new Timestamp(_ts), new Timestamp(_ts + 500_000), _sequence,
            new Price(priceRaw), size, side, flags, depth, BuildLevels());
    }

    private BookLevels BuildLevels()
    {
        var levels = BookLevels.CreateEmpty();
        for (int i = 0; i < BookLevels.Depth; i++)
        {
            var bid = i < _bids.Count ? _bids[i] : default;
            var ask = i < _asks.Count ? _asks[i] : default;
            levels[i] = new BidAskLevel(
                BidPrice: i < _bids.Count ? new Price(bid.PriceRaw) : Price.Undefined,
                AskPrice: i < _asks.Count ? new Price(ask.PriceRaw) : Price.Undefined,
                BidSize: i < _bids.Count ? bid.Size : 0,
                AskSize: i < _asks.Count ? ask.Size : 0,
                BidCount: i < _bids.Count ? bid.Count : 0,
                AskCount: i < _asks.Count ? ask.Count : 0);
        }
        return levels;
    }
}
