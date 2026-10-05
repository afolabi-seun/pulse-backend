using Pulse.Domain.Epics;

namespace Pulse.Application.Epics;

public record EpicDto(
    Guid Id,
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    string Status,
    Guid ProjectId,
    Guid? SprintId,
    int Order,
    DateTime CreatedAt,
    int TotalTasks,
    int CompletedTasks)
{
    public static EpicDto From(Epic e, int totalTasks = 0, int completedTasks = 0) => new(
        e.Id, e.Title, e.Description, e.AcceptanceCriteria,
        e.Status.ToString(), e.ProjectId, e.SprintId,
        e.Order, e.CreatedAt, totalTasks, completedTasks);
}
