using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.TimeEntries;

/// <summary>
/// Who may view a given engineer's time entries. Mirrors <c>CheckInVisibility</c>: engineers see
/// only their own; managers/heads see per their scope (PMO/PM/HeadOfProduct: all; department
/// heads: their department; team leads: their team; Executive/HR/Accountant: all, read-only).
/// </summary>
public static class TimeEntryVisibility
{
    public static async Task<bool> CanViewAsync(
        Guid actorId, string actorRole, Guid targetEngineerId,
        IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        if (targetEngineerId == actorId) return true;                 // always your own
        if (actorRole is Roles.Engineer or Roles.Designer) return false; // individual contributors: own only
        if (actorRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct) return true; // org-wide
        // Executive, HR and Accountant review everyone's timesheets, read-only — the same org-wide view
        // of the Time Summary totals they already have, down to the individual entries.
        if (Roles.IsOrgReadOnlyViewer(actorRole)) return true;

        var target = await engineers.GetByIdAsync(targetEngineerId, ct);
        if (target is null) return false;

        if (Roles.HeadRoles.Contains(actorRole))
        {
            // Department heads: target must be on a team in the same department.
            // A head with no team / no department is unscoped (matches GetTimeEntrySummaryQuery).
            var caller = await engineers.GetByIdAsync(actorId, ct);
            if (caller?.TeamId is not Guid callerTeamId) return true;
            var callerTeam = await teams.GetByIdAsync(callerTeamId, ct);
            if (callerTeam?.Department is not string dept) return true;

            var deptTeamIds = (await teams.ListAllAsync(ct))
                .Where(t => t.Department == dept)
                .Select(t => t.Id)
                .ToHashSet();
            return target.TeamId.HasValue && deptTeamIds.Contains(target.TeamId.Value);
        }

        // Team leads (and anything else that got past the controller gate): same team only.
        var leadCaller = await engineers.GetByIdAsync(actorId, ct);
        return leadCaller?.TeamId is not null && leadCaller.TeamId == target.TeamId;
    }
}
