using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

/// <summary>
/// Shared "can this actor reject this QA review" check, used both to enforce the rule
/// (RejectQaCommand) and to expose it to callers ahead of time (TaskDto.CanRejectQa) so the
/// frontend can disable the action instead of only failing after the fact.
///
/// Only the QA reviewer themselves, the head of the QA reviewer's department, or PMO may
/// reject — deliberately narrower than general project access (which would otherwise let any
/// team lead, department head of the project, or team member reject a review they had no part
/// in). Rejecting your own submitted work doesn't fit the QA-review model, so unlike accepting
/// an unassigned QA task, there's no self-service carve-out here — with no reviewer assigned,
/// only PMO can reject.
/// </summary>
public static class QaRejectionPolicy
{
    public static async Task<bool> CanRejectAsync(
        PulseTask qaTask, Guid actorId, string actorRole,
        IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        if (qaTask.AssigneeId == actorId)
            return true;
        if (actorRole == Roles.HeadOfPmo)
            return true;
        if (qaTask.AssigneeId is not Guid reviewerId)
            return false;

        var reviewer = await engineers.GetByIdAsync(reviewerId, ct);
        var reviewerTeam = reviewer?.TeamId is Guid reviewerTeamId ? await teams.GetByIdAsync(reviewerTeamId, ct) : null;
        if (reviewerTeam?.Department is not string reviewerDept)
            return false;

        if (!ProjectAccessPolicy.DepartmentHeadRoles.Contains(actorRole))
            return false;

        var actor = await engineers.GetByIdAsync(actorId, ct);
        var actorTeam = actor?.TeamId is Guid actorTeamId ? await teams.GetByIdAsync(actorTeamId, ct) : null;
        return actorTeam?.Department == reviewerDept;
    }
}
