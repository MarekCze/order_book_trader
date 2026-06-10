using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;
using OrderFlow.Infrastructure.Storage;

namespace OrderFlow.Tests;

public class SqliteStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Directory.CreateTempSubdirectory("orderflow-sqlite-").FullName, "state.db");

    private static Price Px(decimal p) => Price.FromDecimal(p);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);

    [Fact]
    public void NakedPocs_PersistAcrossReopen()
    {
        using (var store = new SqliteFeatureStateStore(_dbPath))
        {
            store.Add(new DateOnly(2026, 6, 1), Px(5000.00m));
            store.Add(new DateOnly(2026, 6, 2), Px(5020.00m));
            store.MarkFilled(Px(5000.00m), new DateOnly(2026, 6, 3));
        }

        using var reopened = new SqliteFeatureStateStore(_dbPath);
        var active = reopened.Active();
        Assert.Single(active);
        Assert.Equal(Px(5020.00m), active[0].Price);
        Assert.Equal(new DateOnly(2026, 6, 2), active[0].SessionDate);
    }

    [Fact]
    public void Active_OrderedBySessionDateThenPrice()
    {
        using var store = new SqliteFeatureStateStore(_dbPath);
        store.Add(new DateOnly(2026, 6, 2), Px(5020.00m));
        store.Add(new DateOnly(2026, 6, 1), Px(5010.00m));
        store.Add(new DateOnly(2026, 6, 1), Px(5000.00m));

        var active = store.Active();
        Assert.Equal(new[] { Px(5000.00m), Px(5010.00m), Px(5020.00m) }, active.Select(p => p.Price).ToArray());
    }

    [Fact]
    public void AtrSamples_PersistAndFilterByDate()
    {
        using (var store = new SqliteFeatureStateStore(_dbPath))
        {
            store.AddSample(new DateOnly(2026, 5, 1), 9.0);  // outside lookback
            store.AddSample(new DateOnly(2026, 5, 28), 1.0);
            store.AddSample(new DateOnly(2026, 5, 29), 2.0);
            store.AddSample(new DateOnly(2026, 5, 29), 2.5);
        }

        using var reopened = new SqliteFeatureStateStore(_dbPath);
        Assert.Equal(new[] { 1.0, 2.0, 2.5 }, reopened.SamplesSince(new DateOnly(2026, 5, 28)));
        Assert.Equal(2, reopened.DistinctDaysSince(new DateOnly(2026, 5, 28)));
    }
}
