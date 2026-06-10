using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Prior-session and current-session reference levels (PDH/PDL, ONH/ONL, prior
/// POC/VAH/VAL), refreshed by the feature engine at session rollover and at RTH open.
/// </summary>
public sealed class SessionLevelsLoiProvider : ILoiProvider
{
    private IReadOnlyList<(Price Price, LoiType Type)> _levels = Array.Empty<(Price, LoiType)>();

    public void SetLevels(IReadOnlyList<(Price Price, LoiType Type)> levels) => _levels = levels;

    public bool TryGetNearest(Price price, TickSize tick, out LoiReference loi) =>
        LoiSearch.Nearest(price, tick, _levels, out loi);
}

/// <summary>Naked POCs from the registry as LOIs.</summary>
public sealed class NakedPocLoiProvider : ILoiProvider
{
    private readonly INakedPocStore _store;

    public NakedPocLoiProvider(INakedPocStore store)
    {
        _store = store;
    }

    public bool TryGetNearest(Price price, TickSize tick, out LoiReference loi)
    {
        loi = default;
        bool found = false;
        foreach (var poc in _store.Active())
        {
            var candidate = new LoiReference(poc.Price, LoiType.NakedPoc, price.TicksFrom(poc.Price, tick));
            if (!found || Math.Abs(candidate.SignedDistanceTicks) < Math.Abs(loi.SignedDistanceTicks))
            {
                loi = candidate;
                found = true;
            }
        }
        return found;
    }
}

/// <summary>Developing-session LVNs as LOIs, computed from the live profile on each query.</summary>
public sealed class LvnLoiProvider : ILoiProvider
{
    private readonly Func<SessionProfile?> _profile;

    public LvnLoiProvider(Func<SessionProfile?> profile)
    {
        _profile = profile;
    }

    public bool TryGetNearest(Price price, TickSize tick, out LoiReference loi)
    {
        loi = default;
        if (_profile() is not { } profile)
        {
            return false;
        }
        bool found = false;
        foreach (var lvn in profile.Lvns())
        {
            var candidate = new LoiReference(lvn, LoiType.Lvn, price.TicksFrom(lvn, tick));
            if (!found || Math.Abs(candidate.SignedDistanceTicks) < Math.Abs(loi.SignedDistanceTicks))
            {
                loi = candidate;
                found = true;
            }
        }
        return found;
    }
}

/// <summary>
/// Nearest LOI across several providers; ties on absolute distance resolve to the
/// earliest provider in constructor order (deterministic).
/// </summary>
public sealed class CompositeLoiProvider : ILoiProvider
{
    private readonly ILoiProvider[] _providers;

    public CompositeLoiProvider(params ILoiProvider[] providers)
    {
        _providers = providers;
    }

    public bool TryGetNearest(Price price, TickSize tick, out LoiReference loi)
    {
        loi = default;
        bool found = false;
        foreach (var provider in _providers)
        {
            if (provider.TryGetNearest(price, tick, out var candidate)
                && (!found || Math.Abs(candidate.SignedDistanceTicks) < Math.Abs(loi.SignedDistanceTicks)))
            {
                loi = candidate;
                found = true;
            }
        }
        return found;
    }
}

internal static class LoiSearch
{
    public static bool Nearest(
        Price price, TickSize tick, IReadOnlyList<(Price Price, LoiType Type)> levels, out LoiReference loi)
    {
        loi = default;
        bool found = false;
        for (int i = 0; i < levels.Count; i++)
        {
            var candidate = new LoiReference(levels[i].Price, levels[i].Type, price.TicksFrom(levels[i].Price, tick));
            if (!found || Math.Abs(candidate.SignedDistanceTicks) < Math.Abs(loi.SignedDistanceTicks))
            {
                loi = candidate;
                found = true;
            }
        }
        return found;
    }
}
