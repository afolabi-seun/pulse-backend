namespace Pulse.Domain.Tasks;

public static class TaskStatusExtensions
{
    /// <summary>True if the assignee currently owns this as active work — excludes InQa (handed to QA) and Paused (voluntary hold), as well as Done.</summary>
    public static bool CountsAsActiveWorkload(this TaskStatus status) =>
        status is TaskStatus.Active or TaskStatus.Blocked;
}
