using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Sessions;

/// <summary>
/// CME Globex session calendar for ES. All inputs are UTC event timestamps; Eastern Time
/// conversion is DST-correct via the IANA America/New_York zone. The 17:00-18:00 ET
/// window covers both the daily maintenance break and the pre-open, during which orders
/// rest without matching and the book is legitimately crossed — per CLAUDE.md nothing
/// downstream may ingest book states from it.
/// </summary>
public static class CmeSessions
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly TimeSpan MaintenanceStart = new(17, 0, 0);
    private static readonly TimeSpan GlobexOpen = new(18, 0, 0);
    private static readonly TimeSpan RthOpen = new(9, 30, 0);
    private static readonly TimeSpan RthClose = new(16, 0, 0);

    private static DateTime ToEastern(Timestamp ts) =>
        TimeZoneInfo.ConvertTime(ts.ToDateTimeOffsetUtc(), Eastern).DateTime;

    /// <summary>True within [17:00, 18:00) ET — maintenance break plus pre-open, out-of-session.</summary>
    public static bool IsMaintenanceBreak(Timestamp ts)
    {
        var tod = ToEastern(ts).TimeOfDay;
        return tod >= MaintenanceStart && tod < GlobexOpen;
    }

    /// <summary>
    /// Globex trading date: the session opening at 18:00 ET carries the NEXT calendar
    /// day's date (Sunday 18:00 ET opens Monday's session).
    /// </summary>
    public static DateOnly SessionDate(Timestamp ts)
    {
        var et = ToEastern(ts);
        var date = DateOnly.FromDateTime(et);
        return et.TimeOfDay >= GlobexOpen ? date.AddDays(1) : date;
    }

    /// <summary>True within [09:30, 16:00) ET.</summary>
    public static bool IsRth(Timestamp ts)
    {
        var tod = ToEastern(ts).TimeOfDay;
        return tod >= RthOpen && tod < RthClose;
    }
}
