using Pulse.Domain.Sprints;

namespace Pulse.Application.Retrospectives;

public record RetroDto(
    Guid Id,
    Guid SprintId,
    string WentWell,
    string NeedsImprovement,
    string ActionItems,
    DateTime CreatedAt,
    DateTime? UpdatedAt)
{
    public static RetroDto From(SprintRetrospective r) =>
        new(r.Id, r.SprintId, r.WentWell, r.NeedsImprovement, r.ActionItems, r.CreatedAt, r.UpdatedAt);
}
