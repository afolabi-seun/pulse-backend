namespace Pulse.Application.Common;

/// <summary>
/// Finds the Monday that starts the (Monday-first) week containing a date. Shared by every
/// feature that normalizes a date to "the week it falls in" — time entry summaries/reminders,
/// the PMO and Leadership reports, and weekly report submission/drafting all did this inline as
/// <c>date.AddDays(-(int)date.DayOfWeek + (int)DayOfWeek.Monday)</c>, which is wrong specifically
/// on a Sunday: <see cref="DayOfWeek"/> numbers Sunday as 0, so that formula computes
/// <c>date.AddDays(+1)</c> — tomorrow, the *next* week's Monday — instead of 6 days back to the
/// Monday that actually started the current week. Every call site silently used the wrong week
/// once a week, and nobody noticed until it happened to be exercised on an actual Sunday.
/// </summary>
public static class WeekOf
{
    public static DateOnly Monday(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
