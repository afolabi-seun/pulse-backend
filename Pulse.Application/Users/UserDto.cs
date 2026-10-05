using Pulse.Domain.Engineers;

namespace Pulse.Application.Users;

/// <summary>Full admin view of a user — returned to any department head.</summary>
public record UserDto(
    Guid Id,
    string Name,
    string Email,
    string Role,
    bool IsActive,
    int FailedLoginAttempts,
    DateTime? LockedUntil,
    int BaselinePoints,
    int BaselineCycleDays,
    string? Team,
    Guid? TeamId,
    bool IsQa,
    string? Discipline,
    DateTime CreatedAt)
{
    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];

    public static UserDto From(Engineer e) => new(
        e.Id, e.Name, e.Email, e.Role, e.IsActive,
        e.FailedLoginAttempts, e.LockedUntil,
        e.BaselinePoints, e.BaselineCycleDays, e.Team, e.TeamId,
        e.IsQa, e.Discipline.HasValue ? Camel(e.Discipline.Value.ToString()) : null,
        e.CreatedAt);
}
