using OrderFlow.Domain.Features;

namespace OrderFlow.Tests;

public class SessionDistributionTests
{
    [Fact]
    public void NotReady_BeforeMinSamples_QueriesReturnNull()
    {
        var dist = new SessionDistribution(0, 1000, minSamples: 200);
        for (int i = 0; i < 199; i++)
        {
            dist.Add(i % 100);
        }

        Assert.False(dist.IsReady);
        Assert.Null(dist.Quantile(0.95));
        Assert.Null(dist.ZScore(50));
        Assert.Null(dist.PercentileRank(50));
    }

    [Fact]
    public void Ready_AtMinSamples()
    {
        var dist = new SessionDistribution(0, 1000, minSamples: 200);
        for (int i = 0; i < 200; i++)
        {
            dist.Add(i % 100);
        }

        Assert.True(dist.IsReady);
        Assert.NotNull(dist.Quantile(0.95));
    }

    [Fact]
    public void Quantile_OfUniformSequence_IsExact()
    {
        var dist = new SessionDistribution(1, 1000, minSamples: 200);
        for (int v = 1; v <= 1000; v++)
        {
            dist.Add(v);
        }

        // smallest v with count(<= v) >= ceil(p * N)
        Assert.Equal(950, dist.Quantile(0.95));
        Assert.Equal(500, dist.Quantile(0.50));
        Assert.Equal(1, dist.Quantile(0.001));
        Assert.Equal(1000, dist.Quantile(1.0));
    }

    [Fact]
    public void PercentileRank_IsFractionAtOrBelow()
    {
        var dist = new SessionDistribution(1, 100, minSamples: 10);
        for (int v = 1; v <= 100; v++)
        {
            dist.Add(v);
        }

        Assert.Equal(0.95, dist.PercentileRank(95)!.Value, precision: 10);
        Assert.Equal(1.0, dist.PercentileRank(100)!.Value, precision: 10);
        Assert.Equal(0.01, dist.PercentileRank(1)!.Value, precision: 10);
    }

    [Fact]
    public void ZScore_OfKnownDistribution_MatchesSampleStatistics()
    {
        var dist = new SessionDistribution(-1000, 1000, minSamples: 4);
        // values 2, 4, 4, 4, 5, 5, 7, 9: mean 5, sample std sqrt(32/7)
        foreach (var v in new long[] { 2, 4, 4, 4, 5, 5, 7, 9 })
        {
            dist.Add(v);
        }

        double expectedStd = Math.Sqrt(32.0 / 7.0);
        Assert.Equal((9.0 - 5.0) / expectedStd, dist.ZScore(9)!.Value, precision: 10);
        Assert.Equal(0.0, dist.ZScore(5)!.Value, precision: 10);
    }

    [Fact]
    public void ZScore_ZeroVariance_ReturnsNull()
    {
        var dist = new SessionDistribution(0, 100, minSamples: 4);
        for (int i = 0; i < 10; i++)
        {
            dist.Add(42);
        }

        Assert.Null(dist.ZScore(42));
    }

    [Fact]
    public void Add_OutOfRangeValues_AreClampedNotDropped()
    {
        var dist = new SessionDistribution(0, 10, minSamples: 2);
        dist.Add(-5);   // clamps to 0
        dist.Add(50);   // clamps to 10
        dist.Add(5);

        Assert.Equal(3, dist.Count);
        Assert.Equal(10, dist.Quantile(1.0));
        Assert.Equal(0, dist.Quantile(0.01));
    }

    [Fact]
    public void StartNextSession_SeedsBaseline_ReadyImmediately()
    {
        var prior = new SessionDistribution(1, 1000, minSamples: 200);
        for (int v = 1; v <= 1000; v++)
        {
            prior.Add(v);
        }

        var next = prior.StartNextSession();

        Assert.True(next.IsReady);
        Assert.Equal(0, next.Count);                 // live samples reset
        Assert.Equal(950, next.Quantile(0.95));      // answers from prior baseline
    }

    [Fact]
    public void StartNextSession_LiveDistributionTakesOverAtMinSamples()
    {
        var prior = new SessionDistribution(1, 1000, minSamples: 100);
        for (int v = 1; v <= 1000; v++)
        {
            prior.Add(v);
        }

        var next = prior.StartNextSession();
        for (int i = 0; i < 100; i++)
        {
            next.Add(7);    // live session is all 7s
        }

        Assert.Equal(7, next.Quantile(0.95));        // live now governs
    }

    [Fact]
    public void StartNextSession_WithoutEnoughPriorSamples_NotReady()
    {
        var prior = new SessionDistribution(1, 1000, minSamples: 200);
        prior.Add(5);

        var next = prior.StartNextSession();

        Assert.False(next.IsReady);
        Assert.Null(next.Quantile(0.5));
    }
}
