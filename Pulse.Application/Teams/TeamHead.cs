using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Teams;

/// <summary>Who may be named as a team's head: an active person holding one of the department-head roles.</summary>
public static class TeamHead
{
    public record Problem(string Code, string Message);

    public static async Task<Problem?> ValidateAsync(Guid headId, IEngineerRepository engineers, CancellationToken ct)
    {
        var head = await engineers.GetByIdAsync(headId, ct);
        if (head is null || !head.IsActive)
            return new Problem("NOT_FOUND", "That person was not found, or is no longer active.");
        if (!Roles.HeadRoles.Contains(head.Role))
            return new Problem("VALIDATION_ERROR", "A team's head must hold a department-head role.");
        return null;
    }
}
