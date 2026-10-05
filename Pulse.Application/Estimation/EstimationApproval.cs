using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Estimation;

public enum ApprovalStage { TeamLead, DepartmentHead }

/// <summary>Who may currently act on a pending estimate, and which tier that is — <see cref="Stage"/>
/// is <see cref="ApprovalStage.TeamLead"/> only while the Team Lead is the sole eligible approver;
/// once escalated it's <see cref="ApprovalStage.DepartmentHead"/> even though the Team Lead is still
/// included in <see cref="Approvers"/> (escalation adds the head, it doesn't revoke the Team Lead).</summary>
public record ApprovalState(IReadOnlyList<Engineer> Approvers, ApprovalStage Stage);

/// <summary>Who may approve or reject a pending Planning Poker estimate — tiered: the assignee's own
/// Team Lead first, with the department head(s) as a backstop once <paramref name="escalatedToHead"/>
/// (set by EstimateApprovalEscalationScanner after a grace period) or when there's no Team-Lead tier
/// to begin with (the assignee IS a Team Lead, or has no resolvable Team Lead at all) — or, when no
/// department head can be resolved either, a Team Lead+ fallback, so a task never ends up with an
/// estimate nobody can act on. See docs/planning-poker-approval-tiers-spec.md.</summary>
public static class EstimationApproval
{
    public static async Task<ApprovalState?> ResolveApproversAsync(
        Guid? assigneeId, bool escalatedToHead,
        IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        if (assigneeId is not Guid id) return null; // no assignee — caller's own TeamLeadOrAbove fallback applies

        var assignee = await engineers.GetByIdAsync(id, ct);
        // A Team Lead's own task has no "their team lead" tier — there's no lead above a lead —
        // so it goes straight to the department head.
        var skipTeamLeadTier = assignee?.Role == Roles.TeamLead;

        var teamLead = skipTeamLeadTier ? null : await DepartmentScope.GetOwnTeamLeadAsync(id, engineers, teams, ct);

        if (teamLead is not null && !escalatedToHead)
            return new([teamLead], ApprovalStage.TeamLead);

        var heads = await DepartmentScope.GetDepartmentHeadsAsync(id, engineers, teams, ct);
        var approvers = teamLead is not null
            // Escalated: both the Team Lead and the head(s) can act — a backstop, not a handoff.
            ? heads.Cast<Engineer>().Append(teamLead).DistinctBy(e => e.Id).ToList()
            // No Team Lead tier to begin with (skipped, or none resolvable).
            : heads.Cast<Engineer>().ToList();

        return new(approvers, ApprovalStage.DepartmentHead);
    }

    public static async Task<bool> IsAuthorizedAsync(
        Guid? assigneeId, Guid actorId, string actorRole, bool escalatedToHead,
        IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var state = await ResolveApproversAsync(assigneeId, escalatedToHead, engineers, teams, ct);
        if (state is null || state.Approvers.Count == 0)
            return CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(actorRole);

        return state.Approvers.Any(a => a.Id == actorId);
    }
}
