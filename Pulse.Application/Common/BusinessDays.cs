namespace Pulse.Application.Common;

/// <summary>
/// Adds N business days (skipping Saturday/Sunday) to a date. Shared by any feature that needs
/// a weekend-aware date calculation — currently just the QA lead-time due date, but written as
/// a shared helper rather than inline math so future weekend-aware date logic reuses it instead
/// of drifting.
/// </summary>
public static class BusinessDays
{
    public static DateOnly Add(DateOnly start, int businessDays)
    {
        var date = start;
        var step = businessDays >= 0 ? 1 : -1;
        var remaining = Math.Abs(businessDays);

        while (remaining > 0)
        {
            date = date.AddDays(step);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                remaining--;
        }

        return date;
    }

    /// <summary>Weekdays elapsed from <paramref name="from"/> (exclusive) to <paramref name="to"/>
    /// (inclusive) — the inverse of <see cref="Add"/>: <c>Between(d, Add(d, n)) == n</c>. Zero when
    /// <paramref name="to"/> is not after <paramref name="from"/>.</summary>
    public static int Between(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var date = from.AddDays(1); date <= to; date = date.AddDays(1))
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        return count;
    }
}
