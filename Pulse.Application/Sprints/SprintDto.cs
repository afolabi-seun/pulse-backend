using Pulse.Domain.Sprints;

namespace Pulse.Application.Sprints;

public record SprintDto(
    Guid Id,
    Guid TeamId,
    Guid? ProjectId,
    string? ProjectName,
    string Name,
    string? Goal,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    int? CapacityPoints,
    DateOnly? ShowAndTellDate,
    string? ShowAndTellNotes,
    DateTime CreatedAt,
    string? TeamName = null)
{
    public static SprintDto From(Sprint s, string? projectName = null, string? teamName = null) => new(
        s.Id, s.TeamId, s.ProjectId, projectName, s.Name, s.Goal,
        s.StartDate, s.EndDate, s.Status.ToString(),
        s.CapacityPoints, s.ShowAndTellDate, s.ShowAndTellNotes, s.CreatedAt, teamName);
}

public record SprintVelocityDto(
    int PlannedPoints,
    int DeliveredPoints,
    int TotalTasks,
    int DoneTasks);
