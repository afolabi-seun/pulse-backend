namespace Pulse.Application.Common;

/// <summary>
/// How much of the working week lies between two moments — Monday to Friday, between a start and an end hour (UTC). Anything waiting on a person
/// (an estimate to approve) should only run its clock while people are at work: a request made at 18:30 on a Friday has not been ignored by Saturday.
/// </summary>
public static class WorkingHours
{
    public static TimeSpan Between(DateTime fromUtc, DateTime toUtc, int startHour, int endHour)
    {
        if (toUtc <= fromUtc || endHour <= startHour) return TimeSpan.Zero;

        var total = TimeSpan.Zero;
        for (var day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var open = day.AddHours(startHour);
            var close = day.AddHours(endHour);
            var from = fromUtc > open ? fromUtc : open;
            var to = toUtc < close ? toUtc : close;
            if (to > from) total += to - from;
        }

        return total;
    }
}
