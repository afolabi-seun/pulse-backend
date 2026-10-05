using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Feedback;

/// <summary>Whose feedback a reader may see: PMO, Project Manager and HR see every department's; a department
/// head sees their own department's. Shared by the entries list and the patterns summary so the two tabs of the
/// inbox always cover the same people.</summary>
public static class FeedbackScope
{
    /// <summary>The department the caller is limited to, or null for organisation-wide.</summary>
    public static async Task<string?> ResolveDepartmentAsync(
        string role, Guid actorId, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        // HR is org-wide by design (see HrRead's doc comment) — explicit rather than relying on HR engineers
        // happening to have no TeamId, which would otherwise fall through to org-wide by accident.
        if (role is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HR)
            return null;

        var caller = await engineers.GetByIdAsync(actorId, ct);
        if (caller?.TeamId is not Guid teamId)
            return null;

        var team = await teams.GetByIdAsync(teamId, ct);
        return team?.Department;
    }
}
