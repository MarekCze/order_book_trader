using OrderFlow.Domain.Features;
using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class LoiProviderTests
{
    private static readonly TickSize Tick = TickSize.Es;

    private static Price Px(decimal p) => Price.FromDecimal(p);

    // ----- session levels provider -----

    [Fact]
    public void SessionLevels_ReturnsNearestOfItsLevels()
    {
        var provider = new SessionLevelsLoiProvider();
        provider.SetLevels(new[]
        {
            (Px(5050.00m), LoiType.PriorDayHigh),
            (Px(4950.00m), LoiType.PriorDayLow),
            (Px(5010.00m), LoiType.PriorPoc),
        });

        Assert.True(provider.TryGetNearest(Px(5008.00m), Tick, out var loi));
        Assert.Equal(LoiType.PriorPoc, loi.Type);
        Assert.Equal(-8, loi.SignedDistanceTicks); // 2 points below the prior POC
    }

    [Fact]
    public void SessionLevels_EmptyHasNothing()
    {
        var provider = new SessionLevelsLoiProvider();
        Assert.False(provider.TryGetNearest(Px(5000.00m), Tick, out _));
    }

    // ----- naked POC store + provider -----

    [Fact]
    public void NakedPocStore_AddFillActive()
    {
        var store = new InMemoryNakedPocStore();
        store.Add(new DateOnly(2026, 6, 1), Px(5000.00m));
        store.Add(new DateOnly(2026, 6, 2), Px(5020.00m));

        Assert.Equal(2, store.Active().Count);

        store.MarkFilled(Px(5000.00m), new DateOnly(2026, 6, 3));

        var active = store.Active();
        Assert.Single(active);
        Assert.Equal(Px(5020.00m), active[0].Price);
    }

    [Fact]
    public void NakedPocProvider_NearestActiveOnly()
    {
        var store = new InMemoryNakedPocStore();
        store.Add(new DateOnly(2026, 6, 1), Px(5000.00m));
        store.Add(new DateOnly(2026, 6, 2), Px(5020.00m));
        store.MarkFilled(Px(5020.00m), new DateOnly(2026, 6, 3));

        var provider = new NakedPocLoiProvider(store);
        Assert.True(provider.TryGetNearest(Px(5019.00m), Tick, out var loi));
        Assert.Equal(LoiType.NakedPoc, loi.Type);
        Assert.Equal(Px(5000.00m), loi.Price); // 5020 is filled, not an LOI any more
    }

    // ----- LVN provider over the developing profile -----

    [Fact]
    public void LvnProvider_UsesCurrentProfileLvns()
    {
        var profile = new SessionProfile(Tick, new FeatureEngineOptions());
        profile.AddTrade(Px(5000.00m), 40);
        profile.AddTrade(Px(5000.25m), 2);
        profile.AddTrade(Px(5000.50m), 40);

        var provider = new LvnLoiProvider(() => profile);
        Assert.True(provider.TryGetNearest(Px(5000.50m), Tick, out var loi));
        Assert.Equal(LoiType.Lvn, loi.Type);
        Assert.Equal(Px(5000.25m), loi.Price);
        Assert.Equal(1, loi.SignedDistanceTicks);
    }

    // ----- composite -----

    [Fact]
    public void Composite_PicksSmallestAbsoluteDistance_TieGoesToFirstProvider()
    {
        var levels = new SessionLevelsLoiProvider();
        levels.SetLevels(new[] { (Px(5010.00m), LoiType.PriorDayHigh) });
        var round = new RoundNumberLoiProvider(25.00m);

        var composite = new CompositeLoiProvider(levels, round);

        // 5008: 8 ticks below PDH 5010 vs 32 ticks above round 5000 → PDH wins
        Assert.True(composite.TryGetNearest(Px(5008.00m), Tick, out var loi));
        Assert.Equal(LoiType.PriorDayHigh, loi.Type);

        // 5002: 32 ticks below PDH vs 8 ticks above round 5000 → round number wins
        Assert.True(composite.TryGetNearest(Px(5002.00m), Tick, out loi));
        Assert.Equal(LoiType.RoundNumber, loi.Type);

        // 5005: 20 ticks to both → tie resolves to the first provider (PDH)
        Assert.True(composite.TryGetNearest(Px(5005.00m), Tick, out loi));
        Assert.Equal(LoiType.PriorDayHigh, loi.Type);
    }
}
