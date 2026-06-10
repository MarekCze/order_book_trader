using System.Globalization;

namespace OrderFlow.Domain.Primitives;

/// <summary>
/// Fixed-precision price stored as a signed 64-bit integer in 1e-9 currency units,
/// exactly as encoded on the DBN wire (DBN <c>price: i64</c>, 1 unit = 1e-9; see
/// Databento DBN standards doc, "Common fields → price"). ES 5000.00 = 5_000_000_000_000.
/// All arithmetic that cares about the instrument grid goes through <see cref="TickSize"/>.
/// </summary>
public readonly record struct Price(long RawNano) : IComparable<Price>
{
    public const long NanoPerPoint = 1_000_000_000L;

    /// <summary>DBN UNDEF_PRICE sentinel (i64::MAX).</summary>
    public static readonly Price Undefined = new(long.MaxValue);

    public bool IsUndefined => RawNano == long.MaxValue;

    public static Price FromDecimal(decimal points) =>
        new((long)decimal.Round(points * NanoPerPoint, 0, MidpointRounding.AwayFromZero));

    public decimal ToDecimal() => (decimal)RawNano / NanoPerPoint;

    public int CompareTo(Price other) => RawNano.CompareTo(other.RawNano);

    public static bool operator <(Price a, Price b) => a.RawNano < b.RawNano;
    public static bool operator >(Price a, Price b) => a.RawNano > b.RawNano;
    public static bool operator <=(Price a, Price b) => a.RawNano <= b.RawNano;
    public static bool operator >=(Price a, Price b) => a.RawNano >= b.RawNano;

    /// <summary>Price shifted by a signed number of ticks on the instrument grid.</summary>
    public Price AddTicks(long ticks, TickSize tick) => new(RawNano + ticks * tick.RawNano);

    /// <summary>
    /// Signed distance from <paramref name="reference"/> to this price in ticks.
    /// Exact only when both prices are tick-aligned; otherwise truncates toward zero.
    /// </summary>
    public long TicksFrom(Price reference, TickSize tick) => (RawNano - reference.RawNano) / tick.RawNano;

    public bool IsAlignedTo(TickSize tick) => RawNano % tick.RawNano == 0;

    public override string ToString() =>
        IsUndefined ? "UNDEF" : ToDecimal().ToString(CultureInfo.InvariantCulture);
}

/// <summary>Minimum price increment of an instrument, in the same 1e-9 units as <see cref="Price"/>.</summary>
public readonly record struct TickSize(long RawNano)
{
    /// <summary>ES / MES: 0.25 points.</summary>
    public static readonly TickSize Es = new(250_000_000L);

    public static TickSize FromDecimal(decimal points) =>
        new((long)decimal.Round(points * Price.NanoPerPoint, 0, MidpointRounding.AwayFromZero));

    public decimal ToDecimal() => (decimal)RawNano / Price.NanoPerPoint;

    public override string ToString() => ToDecimal().ToString(CultureInfo.InvariantCulture);
}
