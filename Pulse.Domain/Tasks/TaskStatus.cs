namespace Pulse.Domain.Tasks;

public enum TaskStatus
{
    /// <summary>No assignee yet — not real work in progress, so it's excluded from escalation
    /// and from "active workload" (<see cref="TaskStatusExtensions.CountsAsActiveWorkload"/>).
    /// Transitions to <see cref="Active"/> the moment someone is assigned.</summary>
    Backlog,
    Active,
    Blocked,
    InQa,
    Done,
    Paused
}
