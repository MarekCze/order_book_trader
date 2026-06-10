using OrderFlow.Domain.Primitives;

namespace OrderFlow.Tests;

public class PriceTests
{
    [Fact]
    public void FromDecimal_StoresNanoUnits()
    {
        Assert.Equal(5_000_000_000_000L, Price.FromDecimal(5000.00m).RawNano);
        Assert.Equal(250_000_000L, Price.FromDecimal(0.25m).RawNano);
        Assert.Equal(-1_250_000_000L, Price.FromDecimal(-1.25m).RawNano);
    }

    [Fact]
    public void DecimalRoundTrip_IsExactOnTickGrid()
    {
        for (decimal px = 4999.00m; px <= 5001.00m; px += 0.25m)
        {
            Assert.Equal(px, Price.FromDecimal(px).ToDecimal());
        }
    }

    [Fact]
    public void EsTickSize_IsQuarterPoint()
    {
        Assert.Equal(250_000_000L, TickSize.Es.RawNano);
        Assert.Equal(0.25m, TickSize.Es.ToDecimal());
    }

    [Fact]
    public void AddTicks_MovesOnGrid()
    {
        var p = Price.FromDecimal(5000.00m);
        Assert.Equal(Price.FromDecimal(5000.75m), p.AddTicks(3, TickSize.Es));
        Assert.Equal(Price.FromDecimal(4998.50m), p.AddTicks(-6, TickSize.Es));
    }

    [Fact]
    public void TicksFrom_IsSignedTickDistance()
    {
        var l = Price.FromDecimal(5000.00m);
        Assert.Equal(6, Price.FromDecimal(5001.50m).TicksFrom(l, TickSize.Es));
        Assert.Equal(-2, Price.FromDecimal(4999.50m).TicksFrom(l, TickSize.Es));
        Assert.Equal(0, l.TicksFrom(l, TickSize.Es));
    }

    [Fact]
    public void Alignment_DetectsOffGridPrices()
    {
        Assert.True(Price.FromDecimal(5000.25m).IsAlignedTo(TickSize.Es));
        Assert.False(Price.FromDecimal(5000.10m).IsAlignedTo(TickSize.Es));
    }

    [Fact]
    public void Undefined_MatchesDbnSentinel()
    {
        Assert.Equal(long.MaxValue, Price.Undefined.RawNano);
        Assert.True(Price.Undefined.IsUndefined);
        Assert.False(Price.FromDecimal(5000m).IsUndefined);
    }

    [Fact]
    public void Comparisons_OrderByValue()
    {
        var lo = Price.FromDecimal(4999.75m);
        var hi = Price.FromDecimal(5000.00m);
        var loCopy = Price.FromDecimal(4999.75m);
        Assert.True(lo < hi);
        Assert.True(hi > lo);
        Assert.True(lo <= loCopy);
        Assert.True(loCopy >= lo);
        Assert.False(hi <= lo);
    }
}

public class TimestampTests
{
    [Fact]
    public void EpochConversion_IsUtc()
    {
        var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 1, 5, 14, 30, 0, TimeSpan.Zero));
        Assert.Equal(1_767_623_400_000_000_000UL, ts.UnixNanos);
        Assert.Equal("2026-01-05T14:30:00.0000000Z", ts.ToString());
    }

    [Fact]
    public void NanosSince_IsSignedDifference()
    {
        var t0 = new Timestamp(1_000);
        var t1 = new Timestamp(4_500);
        Assert.Equal(3_500, t1.NanosSince(t0));
        Assert.Equal(-3_500, t0.NanosSince(t1));
    }

    [Fact]
    public void AddNanos_AndOrdering()
    {
        var t = new Timestamp(1_000_000);
        Assert.True(t < t.AddNanos(1));
        Assert.Equal(new Timestamp(999_999), t.AddNanos(-1));
    }

    [Fact]
    public void Undefined_MatchesDbnSentinel()
    {
        Assert.True(Timestamp.Undefined.IsUndefined);
        Assert.Equal("UNDEF", Timestamp.Undefined.ToString());
    }
}
