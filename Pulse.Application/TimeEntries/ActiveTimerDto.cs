using Pulse.Domain.TimeEntries;

namespace Pulse.Application.TimeEntries;

public record ActiveTimerDto(Guid Id, string Category, Guid? TaskId, string? TaskTitle, DateTime StartedAt, Guid? SubtaskId = null, string? SubtaskTitle = null)
{
    public static ActiveTimerDto From(ActiveTimer t, string? taskTitle, string? subtaskTitle = null) =>
        new(t.Id, Camel(t.Category.ToString()), t.TaskId, taskTitle, t.StartedAt, t.SubtaskId, subtaskTitle);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
