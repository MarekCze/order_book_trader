using System.Globalization;

namespace OrderFlow.Domain.Primitives;

/// <summary>
/// Event time as unsigned 64-bit nanoseconds since the UNIX epoch (UTC), exactly as on the
/// DBN wire (<c>ts_event</c>/<c>ts_recv</c>: u64). This is the ONLY source of time for
/// Domain/Application logic — wall-clock time is forbidden there by the determinism rule.
/// </summary>
public readonly record struct Timestamp(ulong UnixNanos) : IComparable<Timestamp>
{
    /// <summary>DBN UNDEF_TIMESTAMP sentinel (u64::MAX).</summary>
    public static readonly Timestamp Undefined = new(ulong.MaxValue);

    public bool IsUndefined => UnixNanos == ulong.MaxValue;

    public int CompareTo(Timestamp other) => UnixNanos.CompareTo(other.UnixNanos);

    public static bool operator <(Timestamp a, Timestamp b) => a.UnixNanos < b.UnixNanos;
    public static bool operator >(Timestamp a, Timestamp b) => a.UnixNanos > b.UnixNanos;
    public static bool operator <=(Timestamp a, Timestamp b) => a.UnixNanos <= b.UnixNanos;
    public static bool operator >=(Timestamp a, Timestamp b) => a.UnixNanos >= b.UnixNanos;

    /// <summary>Signed nanoseconds elapsed since <paramref name="earlier"/>.</summary>
    public long NanosSince(Timestamp earlier) => (long)(UnixNanos - earlier.UnixNanos);

    public Timestamp AddNanos(long nanos) => new((ulong)((long)UnixNanos + nanos));

    public static Timestamp FromDateTimeOffset(DateTimeOffset utc) =>
        new((ulong)((utc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L));

    /// <summary>Display-only conversion (truncates to 100ns ticks). Pure function of the value.</summary>
    public DateTimeOffset ToDateTimeOffsetUtc() =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(UnixNanos / 100));

    public override string ToString() =>
        IsUndefined
            ? "UNDEF"
            : ToDateTimeOffsetUtc().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
