namespace Pulse.Application.Vitals;

// EngineerName is null for GetMyVitalsHistoryQuery (the viewer's own history — no name needed) and
// populated for ListVitalsQuery (a head/PMO/HR viewing other engineers' responses).
public record VitalsDto(Guid Id, Guid EngineerId, int Score, string? Comment, DateOnly WeekOf, DateTime CreatedAt, string? EngineerName = null)
{
    public static VitalsDto From(Domain.Vitals.VitalsResponse p) =>
        new(p.Id, p.EngineerId, p.Score, p.Comment, p.WeekOf, p.CreatedAt);

    public static VitalsDto FromWithName(Domain.Vitals.VitalsResponse p, string? engineerName) =>
        new(p.Id, p.EngineerId, p.Score, p.Comment, p.WeekOf, p.CreatedAt, engineerName);
}
