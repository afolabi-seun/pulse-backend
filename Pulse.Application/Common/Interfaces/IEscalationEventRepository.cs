using Pulse.Domain.Escalations;

namespace Pulse.Application.Common.Interfaces;

public record WeeklyEscalationPoint(DateOnly WeekOf, int Count);

public interface IEscalationEventRepository
{
    Task<bool> AlreadyFiredAsync(Guid taskId, EscalationLevel level, CancellationToken ct = default);
    Task RecordAsync(Guid taskId, EscalationLevel level, CancellationToken ct = default);
    Task ClearForTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<DateTime?> GetLastFiredAtAsync(CancellationToken ct = default);

    /// <summary>Count of distinct tasks assigned to the engineer (optionally scoped to one project) that fired
    /// at least one escalation event within the range — a near-miss signal, not just outright overdue tasks.</summary>
    Task<int> GetEscalatedTaskCountAsync(
        Guid assigneeId, DateTime from, DateTime to, Guid? projectId = null, CancellationToken ct = default);

    /// <summary>Org-wide count of escalation events fired per week over the last 6 weeks — every
    /// level counted (not distinct tasks), so a task that re-escalates from T1 to T3 counts twice,
    /// matching what actually happened operationally that week.</summary>
    Task<IReadOnlyList<WeeklyEscalationPoint>> GetWeeklyEscalationCountAsync(CancellationToken ct = default);
}
