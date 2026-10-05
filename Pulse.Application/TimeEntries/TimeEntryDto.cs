using Pulse.Domain.TimeEntries;

namespace Pulse.Application.TimeEntries;

public record TimeEntryDto(
    Guid Id,
    Guid EngineerId,
    DateOnly Date,
    string Category,
    Guid? TaskId,
    Guid? ProjectId,
    decimal Hours,
    string? Note,
    DateTime LoggedAt,
    Guid? SubtaskId = null,
    // The task's own title, resolved independently of the viewer's current task list — a logged
    // entry is a historical record, so it must still show the real name even once the task is no
    // longer assigned to the viewer (done, back in backlog, reassigned away). Null when the
    // caller didn't resolve it (most single-entry command responses skip it; their result is
    // immediately superseded by a refetch of the real list anyway).
    string? TaskTitle = null,
    // Display key ("NOTIF-011") and project name, resolved by ListTimeEntriesQuery so a viewer reviewing
    // someone else's timesheet (HR, a lead) sees which task and project each entry belongs to.
    string? TaskKey = null,
    string? ProjectName = null)
{
    public static TimeEntryDto From(TimeEntry e, string? taskTitle = null, string? taskKey = null, string? projectName = null) =>
        new(e.Id, e.EngineerId, e.Date, Camel(e.Category.ToString()), e.TaskId, e.ProjectId, e.Hours, e.Note, e.CreatedAt, e.SubtaskId, taskTitle, taskKey, projectName);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
