using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Common;

/// <summary>The people answerable for a task, found through the team and department that OWN it — the team that owns its project — not the ones the
/// assignee happens to sit in. An engineer loaned to another department's project is still working for that project's lead and department head.</summary>
/// <param name="TeamLead">The lead of the owning team, or null when there is none to ask.</param>
/// <param name="DepartmentHeads">Heads of the owning team's department, the PMO head excluded since the PMO is its own tier.</param>
/// <param name="Pmo">Head of PMO and the Project Managers, who answer for every project.</param>
public record TaskStakeholders(Engineer? TeamLead, IReadOnlyList<Engineer> DepartmentHeads, IReadOnlyList<Engineer> Pmo)
{
    /// <summary>When the owning team cannot be found the assignee's own team stands in for it: a project with no owner team (or a personal one)
    /// would otherwise leave nobody to ask.</summary>
    public static async Task<TaskStakeholders> ResolveAsync(
        Guid projectId, Guid? assigneeId,
        IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(projectId, ct);
        Domain.Teams.Team? team = project?.OwnerTeamId is Guid ownerTeamId ? await teams.GetByIdAsync(ownerTeamId, ct) : null;
        if (team is null && assigneeId is Guid id)
        {
            var assignee = await engineers.GetByIdAsync(id, ct);
            if (assignee?.TeamId is Guid assigneeTeamId)
                team = await teams.GetByIdAsync(assigneeTeamId, ct);
        }

        Engineer? lead = null;
        if (team?.TeamLeadId is Guid leadId)
        {
            lead = await engineers.GetByIdAsync(leadId, ct);
            if (lead is { IsActive: false }) lead = null;
        }

        var heads = new List<Engineer>();
        if (team?.Department is string department)
        {
            var departmentTeamIds = (await teams.ListAllAsync(ct)).Where(t => t.Department == department).Select(t => t.Id).ToHashSet();
            heads = (await engineers.ListActiveAsync(ct))
                .Where(e => e.Role != Roles.HeadOfPmo && Roles.HeadRoles.Contains(e.Role) && e.TeamId is Guid t && departmentTeamIds.Contains(t))
                .ToList();
        }

        var pmo = new List<Engineer>();
        foreach (var role in new[] { Roles.HeadOfPmo, Roles.ProjectManager })
            pmo.AddRange(await engineers.ListByRoleAsync(role, ct));

        return new TaskStakeholders(lead, heads, pmo.DistinctBy(e => e.Id).ToList());
    }
}
