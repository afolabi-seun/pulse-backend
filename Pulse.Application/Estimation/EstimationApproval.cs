using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Estimation;

public enum ApprovalStage { TeamLead, DepartmentHead, Pmo }

/// <summary>Who may currently act on a pending estimate, and which tier that is — <see cref="Stage"/> is the highest tier now open. Escalation adds
/// people, it never takes the earlier tier away: once the head is added the Team Lead can still act, and once the PMO is added so can both.</summary>
public record ApprovalState(IReadOnlyList<Engineer> Approvers, ApprovalStage Stage);

/// <summary>Who may approve or reject a pending Planning Poker estimate — tiered, and found through the team and department that OWN the task's project
/// (see <see cref="TaskStakeholders"/>), not the assignee's own: the owning team's lead first, then once <paramref name="reached"/> says so the
/// owning department's head(s), then the PMO. A tier with nobody in it is skipped — the Team Lead tier too when the assignee IS a lead, since there is
/// no lead above a lead — and if neither a lead nor a head can be found the request goes straight to the PMO, so it never waits on nobody. When even
/// the PMO is empty, a Team Lead+ fallback applies (see <see cref="IsAuthorizedAsync"/>). See docs/planning-poker-approval-tiers-spec.md.
///
/// Whoever submitted an estimate can never be the one who approves or rejects it: a Team Lead who submits on an engineer's behalf would otherwise be
/// that engineer's approver too, and the whole point of an approval is a second pair of eyes. The submitter is dropped from every tier.</summary>
public static class EstimationApproval
{
    /// <summary>The highest tier a pending request has been opened up to so far.</summary>
    public static ApprovalStage ReachedBy(Domain.Tasks.TaskEstimationSession session) =>
        session.EscalatedToPmo ? ApprovalStage.Pmo : session.EscalatedToHead ? ApprovalStage.DepartmentHead : ApprovalStage.TeamLead;

    public static async Task<ApprovalState> ResolveApproversAsync(
        Guid projectId, Guid? assigneeId, ApprovalStage reached, Guid? submittedBy,
        IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var stakeholders = await TaskStakeholders.ResolveAsync(projectId, assigneeId, projects, engineers, teams, ct);

        // A team lead's own task has no "their team lead" tier — it goes straight to the department head.
        var assignee = assigneeId is Guid id ? await engineers.GetByIdAsync(id, ct) : null;
        // A lead who submitted it cannot also be the first-line approver of their own submission, so their tier is skipped too.
        var lead = assignee?.Role == Roles.TeamLead || stakeholders.TeamLead?.Id == assigneeId || stakeholders.TeamLead?.Id == submittedBy
            ? null : stakeholders.TeamLead;

        var approvers = new List<Engineer>();
        var stage = ApprovalStage.TeamLead;
        if (lead is not null) approvers.Add(lead);

        var headsOpen = reached >= ApprovalStage.DepartmentHead || lead is null;
        var heads = stakeholders.DepartmentHeads.Where(h => h.Id != submittedBy).ToList();
        if (headsOpen && heads.Count > 0)
        {
            approvers.AddRange(heads);
            stage = ApprovalStage.DepartmentHead;
        }

        // Nobody below the PMO to ask: it goes there from the start rather than waiting on no one.
        if (reached >= ApprovalStage.Pmo || approvers.Count == 0)
        {
            approvers.AddRange(stakeholders.Pmo);
            stage = ApprovalStage.Pmo;
        }

        return new(approvers.DistinctBy(a => a.Id).Where(a => a.Id != submittedBy).ToList(), stage);
    }

    public static async Task<bool> IsAuthorizedAsync(
        Guid projectId, Guid? assigneeId, Guid actorId, string actorRole, ApprovalStage reached, Guid? submittedBy,
        IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        if (actorId == submittedBy) return false;

        var state = await ResolveApproversAsync(projectId, assigneeId, reached, submittedBy, projects, engineers, teams, ct);
        if (state.Approvers.Count == 0)
            return CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(actorRole);

        return state.Approvers.Any(a => a.Id == actorId);
    }
}
