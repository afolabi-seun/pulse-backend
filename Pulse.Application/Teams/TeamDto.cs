using Pulse.Domain.Teams;

namespace Pulse.Application.Teams;

public record TeamDto(Guid Id, string Name, Guid? TeamLeadId, string? TeamLeadName, bool IsActive, string? Department, int? MemberCount)
{
    public static TeamDto From(Team t, string? leadName = null, int? memberCount = null) =>
        new(t.Id, t.Name, t.TeamLeadId, leadName, t.IsActive, t.Department, memberCount);
}
