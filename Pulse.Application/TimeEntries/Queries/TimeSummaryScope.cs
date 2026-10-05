using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;

namespace Pulse.Application.TimeEntries.Queries;

/// <summary>Which engineers a Time Summary caller may see, and therefore whose hours its totals and every drill-down
/// under it cover. Shared by the summary and its drill-downs so what is expanded always adds up to the row.</summary>
internal static class TimeSummaryScope
{
    public static async Task<(IReadOnlyList<Engineer> AllActive, HashSet<Guid> ScopedIds)> ResolveAsync(
        string callerRole, Guid callerId, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var allActive = await engineers.ListActiveAsync(ct);
        HashSet<Guid> scopedIds;

        if (callerRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct or Roles.Executive or Roles.HR or Roles.Accountant)
        {
            scopedIds = allActive.Select(e => e.Id).ToHashSet();
        }
        else if (Roles.HeadRoles.Contains(callerRole))
        {
            // Scope to engineers on teams in the same department as the caller's team.
            // Falls back to all engineers if the caller has no team or their team has no dept set.
            var caller = allActive.FirstOrDefault(e => e.Id == callerId);
            if (caller?.TeamId is Guid callerTeamId)
            {
                var callerTeam = await teams.GetByIdAsync(callerTeamId, ct);
                if (callerTeam?.Department is string deptName)
                {
                    var allTeams = await teams.ListAllAsync(ct);
                    var deptTeamIds = allTeams
                        .Where(t => t.Department == deptName)
                        .Select(t => t.Id)
                        .ToHashSet();
                    scopedIds = allActive
                        .Where(e => e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
                        .Select(e => e.Id)
                        .ToHashSet();
                }
                else
                {
                    scopedIds = allActive.Select(e => e.Id).ToHashSet();
                }
            }
            else
            {
                scopedIds = allActive.Select(e => e.Id).ToHashSet();
            }
        }
        else
        {
            // Team lead — their team only
            var caller = allActive.FirstOrDefault(e => e.Id == callerId);
            scopedIds = caller?.TeamId is not null
                ? allActive.Where(e => e.TeamId == caller.TeamId).Select(e => e.Id).ToHashSet()
                : [callerId];
        }

        // Executive/PMO(head_of_pmo, project_manager)/Accountant can never log time at all (see
        // TimeEntrySubmitter) — without this they'd sit in the roster forever at a permanent 0h,
        // padding "Engineers with zero hours" with people who were never going to log any. Matches
        // the Standup Digest's own CheckInExpected filter for the same kind of report-roster gap.
        var timeEntrySubmitterRoles = CapabilityRegistry.All[CapabilityRegistry.TimeEntrySubmitter].AllowedRoles;
        var roleById = allActive.ToDictionary(e => e.Id, e => e.Role);
        scopedIds = scopedIds.Where(id => roleById.TryGetValue(id, out var role) && timeEntrySubmitterRoles.Contains(role)).ToHashSet();

        return (allActive, scopedIds);
    }
}
