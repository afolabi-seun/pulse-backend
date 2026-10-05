using Pulse.Domain.Common;

namespace Pulse.Domain.TimeEntries;

public class TimeEntry : Entity
{
    public Guid EngineerId { get; private set; }
    public DateOnly Date { get; private set; }
    public TimeEntryCategory Category { get; private set; }
    public Guid? TaskId { get; private set; }
    /// <summary>The specific checklist item this time was logged against, when working a loaned
    /// <see cref="Tasks.Subtask"/> rather than the task as a whole. Always accompanied by TaskId.</summary>
    public Guid? SubtaskId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public decimal Hours { get; private set; }
    public string? Note { get; private set; }

    private TimeEntry() { }

    public static TimeEntry Log(Guid engineerId, DateOnly date, TimeEntryCategory category, Guid? taskId, Guid? projectId, decimal hours, string? note, Guid? subtaskId = null)
    {
        Validate(category, taskId, projectId, hours, subtaskId);
        return new()
        {
            EngineerId = engineerId,
            Date = date,
            Category = category,
            TaskId = taskId,
            SubtaskId = subtaskId,
            ProjectId = projectId,
            Hours = hours,
            Note = note,
        };
    }

    public void Update(DateOnly date, TimeEntryCategory category, Guid? taskId, Guid? projectId, decimal hours, string? note, Guid? subtaskId = null)
    {
        Validate(category, taskId, projectId, hours, subtaskId);
        Date = date;
        Category = category;
        TaskId = taskId;
        SubtaskId = subtaskId;
        ProjectId = projectId;
        Hours = hours;
        Note = note;
    }

    private static void Validate(TimeEntryCategory category, Guid? taskId, Guid? projectId, decimal hours, Guid? subtaskId)
    {
        if (category == TimeEntryCategory.Task && taskId is null)
            throw new DomainException("A task-category time entry requires a TaskId.");
        if (category != TimeEntryCategory.Task && taskId is not null)
            throw new DomainException("Only a task-category entry may reference a task.");
        // A task-category entry's project is already implicit via its task — letting the two
        // drift apart would be a real data-integrity risk, so it isn't settable directly.
        if (category == TimeEntryCategory.Task && projectId is not null)
            throw new DomainException("A task-category entry's project is derived from its task and cannot be set directly.");
        if (subtaskId is not null && taskId is null)
            throw new DomainException("A subtask reference requires a TaskId.");
        if (hours <= 0)
            throw new DomainException("Hours must be greater than zero.");
        if (hours > 24)
            throw new DomainException("A single time entry cannot exceed 24 hours.");
    }
}
