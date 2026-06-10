namespace OrderFlow.Domain.Features;

using OrderFlow.Domain.Primitives;

/// <summary>A prior session's POC that price has not yet revisited.</summary>
public readonly record struct NakedPoc(DateOnly SessionDate, Price Price);

/// <summary>
/// Registry of naked POCs (CLAUDE.md: persisted across sessions in SQLite — the SQLite
/// adapter lives in Infrastructure; this port keeps Domain pure). Implementations must
/// return <see cref="Active"/> in deterministic order (session date, then price).
/// </summary>
public interface INakedPocStore
{
    void Add(DateOnly sessionDate, Price poc);

    void MarkFilled(Price price, DateOnly filledOn);

    IReadOnlyList<NakedPoc> Active();
}

public sealed class InMemoryNakedPocStore : INakedPocStore
{
    private readonly List<NakedPoc> _active = new();

    public void Add(DateOnly sessionDate, Price poc)
    {
        _active.Add(new NakedPoc(sessionDate, poc));
        _active.Sort((a, b) => a.SessionDate != b.SessionDate
            ? a.SessionDate.CompareTo(b.SessionDate)
            : a.Price.CompareTo(b.Price));
    }

    public void MarkFilled(Price price, DateOnly filledOn) =>
        _active.RemoveAll(p => p.Price == price);

    public IReadOnlyList<NakedPoc> Active() => _active;
}
