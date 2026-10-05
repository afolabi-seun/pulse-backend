using Pulse.Domain.CheckIns;

namespace Pulse.Application.CheckIns;

public record CheckInDto(
    Guid Id,
    Guid EngineerId,
    DateOnly Date,
    string Completed,
    string PlannedNext,
    string? Blockers,
    DateTime SubmittedAt,
    Guid? ProjectId = null)
{
    public static CheckInDto From(CheckIn c) =>
        new(c.Id, c.EngineerId, c.Date, c.Completed, c.PlannedNext, c.Blockers, c.CreatedAt, c.ProjectId);
}
