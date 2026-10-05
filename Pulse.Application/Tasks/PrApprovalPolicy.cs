using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

/// <summary>Who may approve, reject, or reassign a pending PR-approval request: the assignee's own
/// department head(s) (see <see cref="DepartmentScope.GetDepartmentHeadsAsync"/>), or — when none
/// can be resolved — a Team Lead+, so a task never ends up with a request nobody can act on. Once
/// the request has been explicitly reassigned (<see cref="PulseTask.ReassignPrApprover"/>), only
/// the delegate may act, so a department head can hand things off before going on leave. Head of
/// PMO and Executive can always act, the same org-wide override <c>LoanTaskCommand</c> grants them,
/// covering the case where the head is simply unavailable and nobody pre-delegated.</summary>
public static class PrApprovalPolicy
{
    public static async Task<bool> IsAuthorizedAsync(
        PulseTask task, Guid actorId, string actorRole,
        IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        if (actorRole is Roles.HeadOfPmo or Roles.Executive)
            return true;

        if (task.PendingPrApprovalDelegatedToEngineerId is Guid delegateId)
            return actorId == delegateId;

        if (task.AssigneeId is not Guid assigneeId)
            return CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(actorRole);

        var heads = await DepartmentScope.GetDepartmentHeadsAsync(assigneeId, engineers, teams, ct);
        return heads.Count == 0
            ? CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(actorRole)
            : heads.Any(h => h.Id == actorId);
    }
}
