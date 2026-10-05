using Pulse.Domain.Common;

namespace Pulse.Domain.TimeEntries;

/// <summary>The one thing currently being timed for an engineer — at most one per engineer at any
/// moment (enforced by the caller: StartTimerCommand stops-and-logs any existing timer before
/// starting a new one, rather than a DB-level uniqueness constraint). Deleted once stopped; the
/// committed result becomes a normal <see cref="TimeEntry"/>.</summary>
public class ActiveTimer : Entity
{
    public Guid EngineerId { get; private set; }
    public TimeEntryCategory Category { get; private set; }
    public Guid? TaskId { get; private set; }
    /// <summary>The specific checklist item being timed, when the engineer is working on a
    /// loaned <see cref="Tasks.Subtask"/> rather than the task as a whole. Always accompanied by
    /// TaskId — never set on its own.</summary>
    public Guid? SubtaskId { get; private set; }
    public DateTime StartedAt { get; private set; }

    private ActiveTimer() { }

    public static ActiveTimer Start(Guid engineerId, TimeEntryCategory category, Guid? taskId, Guid? subtaskId = null)
    {
        if (category != TimeEntryCategory.Task && category != TimeEntryCategory.Meeting)
            throw new DomainException("A timer can only be started for a Task or a Meeting.");
        if (category == TimeEntryCategory.Task && taskId is null)
            throw new DomainException("A task timer requires a TaskId.");
        if (category == TimeEntryCategory.Meeting && taskId is not null)
            throw new DomainException("A meeting timer cannot reference a task.");
        if (subtaskId is not null && taskId is null)
            throw new DomainException("A subtask timer requires a TaskId.");

        return new()
        {
            EngineerId = engineerId,
            Category = category,
            TaskId = taskId,
            SubtaskId = subtaskId,
            StartedAt = DateTime.UtcNow,
        };
    }
}
