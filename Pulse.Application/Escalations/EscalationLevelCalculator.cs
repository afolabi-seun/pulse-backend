using Pulse.Application.Overwork;
using Pulse.Domain.Escalations;

namespace Pulse.Application.Escalations;

public static class EscalationLevelCalculator
{
    /// <summary>
    /// Escalation level as of <paramref name="now"/>. Due-time is end-of-day UTC — matches when
    /// <see cref="EscalationScanner"/> actually fires notifications. Null = not yet escalating.
    /// </summary>
    public static EscalationLevel? Determine(DateOnly dueDate, DateTime activatedAt, OverworkThresholds thresholds, DateTime now)
    {
        var dueDateTime = dueDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var hoursRemaining = (dueDateTime - now).TotalHours;
        var totalDays = (dueDateTime - activatedAt).TotalDays;

        // A task whose clock just restarted (e.g. reactivated from a QA rejection) gets a short
        // grace window before "Overdue" fires, even though its due date is already behind it —
        // otherwise every rejection immediately re-fires the full overdue broadcast (assignee +
        // PMs/dept heads) the instant the task reactivates, before the assignee has had any time
        // to act on the rework. Doesn't apply once the task has been active for a while (that's a
        // genuinely overdue task, not a fresh restart) — see EscalationReactivationGraceHours.
        var hoursSinceActivation = (now - activatedAt).TotalHours;
        if (hoursRemaining <= 0 && hoursSinceActivation < thresholds.EscalationReactivationGraceHours)
            return null;

        if (hoursRemaining <= 0 || totalDays <= 0)
            return EscalationLevel.Overdue;

        var elapsedPct = (now - activatedAt).TotalDays / totalDays;
        var t3Threshold = Math.Max(thresholds.EscalationT3ElapsedPct, 1.0 - thresholds.EscalationT3Days / totalDays);
        var t1Threshold = Math.Max(thresholds.EscalationT1ElapsedPct, 1.0 - thresholds.EscalationT1Days / totalDays);

        if (elapsedPct >= t1Threshold && hoursRemaining >= thresholds.EscalationT1MinHours) return EscalationLevel.TMinus1;
        if (elapsedPct >= t3Threshold && hoursRemaining >= thresholds.EscalationT3MinHours) return EscalationLevel.TMinus3;
        return null;
    }
}
