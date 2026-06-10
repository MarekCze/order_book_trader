using OrderFlow.Domain.Primitives;
using OrderFlow.Domain.Sessions;

namespace OrderFlow.Tests;

public class CmeSessionsTests
{
    private static Timestamp Utc(int y, int mo, int d, int h, int mi, int s = 0) =>
        Timestamp.FromDateTimeOffset(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero));

    // ----- maintenance break / pre-open (17:00-18:00 ET, out-of-session per CLAUDE.md) -----

    [Fact]
    public void MaintenanceBreak_DuringSummerPreOpen_IsTrue()
    {
        // 2026-06-01 21:45 UTC = 17:45 EDT — exactly where real-data crossed books were observed
        Assert.True(CmeSessions.IsMaintenanceBreak(Utc(2026, 6, 1, 21, 45)));
    }

    [Fact]
    public void MaintenanceBreak_AtSummerReopen_IsFalse()
    {
        // 22:00 UTC = 18:00 EDT, the reopen itself is in-session
        Assert.False(CmeSessions.IsMaintenanceBreak(Utc(2026, 6, 1, 22, 0)));
    }

    [Fact]
    public void MaintenanceBreak_AtSummerClose_IsTrue()
    {
        // 21:00 UTC = 17:00 EDT, the close boundary starts the break
        Assert.True(CmeSessions.IsMaintenanceBreak(Utc(2026, 6, 1, 21, 0)));
    }

    [Fact]
    public void MaintenanceBreak_DuringWinterPreOpen_IsTrue()
    {
        // 2026-01-15 22:30 UTC = 17:30 EST (UTC-5)
        Assert.True(CmeSessions.IsMaintenanceBreak(Utc(2026, 1, 15, 22, 30)));
    }

    [Fact]
    public void MaintenanceBreak_DuringWinterRth_IsFalse()
    {
        // 2026-01-15 15:00 UTC = 10:00 EST
        Assert.False(CmeSessions.IsMaintenanceBreak(Utc(2026, 1, 15, 15, 0)));
    }

    // ----- Globex session date (rolls at 18:00 ET) -----

    [Fact]
    public void SessionDate_BeforeSummerReopen_IsSameCalendarDay()
    {
        // Monday 2026-06-01 14:00 UTC = 10:00 EDT → still the June 1 session
        Assert.Equal(new DateOnly(2026, 6, 1), CmeSessions.SessionDate(Utc(2026, 6, 1, 14, 0)));
    }

    [Fact]
    public void SessionDate_AfterSummerReopen_IsNextCalendarDay()
    {
        // Monday 2026-06-01 22:30 UTC = 18:30 EDT → the June 2 session has begun
        Assert.Equal(new DateOnly(2026, 6, 2), CmeSessions.SessionDate(Utc(2026, 6, 1, 22, 30)));
    }

    [Fact]
    public void SessionDate_AfterWinterReopen_IsNextCalendarDay()
    {
        // 2026-01-15 23:30 UTC = 18:30 EST → the January 16 session
        Assert.Equal(new DateOnly(2026, 1, 16), CmeSessions.SessionDate(Utc(2026, 1, 15, 23, 30)));
    }

    [Fact]
    public void SessionDate_OvernightUtcMorning_IsSameSession()
    {
        // 2026-06-02 03:00 UTC = 2026-06-01 23:00 EDT → June 2 session (opened 18:00 EDT June 1)
        Assert.Equal(new DateOnly(2026, 6, 2), CmeSessions.SessionDate(Utc(2026, 6, 2, 3, 0)));
    }

    // ----- RTH (09:30-16:00 ET) -----

    [Fact]
    public void IsRth_SummerOpen_TrueAtOpenFalseJustBefore()
    {
        // 13:30 UTC = 09:30 EDT
        Assert.True(CmeSessions.IsRth(Utc(2026, 6, 1, 13, 30)));
        Assert.False(CmeSessions.IsRth(Utc(2026, 6, 1, 13, 29, 59)));
    }

    [Fact]
    public void IsRth_SummerClose_FalseAtClose()
    {
        // 20:00 UTC = 16:00 EDT — close is exclusive
        Assert.False(CmeSessions.IsRth(Utc(2026, 6, 1, 20, 0)));
        Assert.True(CmeSessions.IsRth(Utc(2026, 6, 1, 19, 59, 59)));
    }

    [Fact]
    public void IsRth_WinterMidSession_True()
    {
        // 2026-01-15 17:00 UTC = 12:00 EST
        Assert.True(CmeSessions.IsRth(Utc(2026, 1, 15, 17, 0)));
    }
}
