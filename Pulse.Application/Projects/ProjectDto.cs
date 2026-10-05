using Pulse.Domain.Projects;

namespace Pulse.Application.Projects;

public record ProjectDto(Guid Id, string Name, string Code, string? Description, bool IsActive, string Status, DateTime CreatedAt, Guid? OwnerTeamId, bool CanAccess)
{
    public static ProjectDto From(Project p, bool canAccess = true) =>
        new(p.Id, p.Name, p.Code, p.Description, p.Status == ProjectStatus.Active, Camel(p.Status.ToString()), p.CreatedAt, p.OwnerTeamId, canAccess);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
