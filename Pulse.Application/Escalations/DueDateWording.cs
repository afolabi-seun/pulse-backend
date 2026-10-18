namespace Pulse.Application.Escalations;

/// <summary>
/// Words for how far away a due date is, worked out from the real distance when the message is written. The escalation stages are named after when they
/// normally fire (3 days and 1 day out), but they are triggered by a share of the task's lifetime having passed as well, so a task given a short deadline
/// reaches its "3 day" stage with a day or less to go. The text must never claim the stage's nominal number: it says what is actually true.
/// Days are counted in UTC, the same clock the escalation scanner and the due moment (end of the due day) use.
/// </summary>
public static class DueDateWording
{
    public static int DaysUntil(DateOnly dueDate, DateTime now) => dueDate.DayNumber - DateOnly.FromDateTime(now).DayNumber;

    /// <summary>"today", "tomorrow", "in 2 days"; for a date already passed, "yesterday" or "3 days ago".</summary>
    public static string When(DateOnly dueDate, DateTime now) => DaysUntil(dueDate, now) switch
    {
        0 => "today",
        1 => "tomorrow",
        -1 => "yesterday",
        var days when days > 1 => $"in {days} days",
        var days => $"{-days} days ago",
    };

    /// <summary>" (yesterday)" or " (3 days ago)" for a due date that has passed, otherwise empty, so an overdue message can say how long ago it was due.</summary>
    public static string OverdueSuffix(DateOnly dueDate, DateTime now) =>
        DaysUntil(dueDate, now) < 0 ? $" ({When(dueDate, now)})" : string.Empty;
}
