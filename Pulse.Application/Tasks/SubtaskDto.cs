using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

public record SubtaskDto(
    Guid Id,
    Guid TaskId,
    string Title,
    bool IsDone,
    Guid CreatedBy,
    Guid? CompletedBy,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    Guid? AssigneeId = null,
    string? AssigneeName = null)
{
    public static SubtaskDto From(Subtask s, string? assigneeName = null) =>
        new(s.Id, s.TaskId, s.Title, s.IsDone, s.CreatedBy, s.CompletedBy, s.CompletedAt, s.CreatedAt, s.AssigneeId, assigneeName);
}
