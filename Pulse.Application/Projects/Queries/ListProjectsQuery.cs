using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListProjectsQuery(Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<ProjectDto>>>;

public class ListProjectsHandler : IRequestHandler<ListProjectsQuery, ServiceResult<IReadOnlyList<ProjectDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectFollowRepository _follows;

    public ListProjectsHandler(IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams,
        IProjectFollowRepository follows)
    {
        _projects = projects;
        _engineers = engineers;
        _teams = teams;
        _follows = follows;
    }

    public async Task<ServiceResult<IReadOnlyList<ProjectDto>>> Handle(ListProjectsQuery query, CancellationToken ct)
    {
        var projects = await _projects.ListActiveAsync(ct);

        // Global roles see every project — no per-row computation needed. Executive gets the same
        // treatment (read-only, org-wide) via its own check rather than joining GlobalRoles itself,
        // since that set is also used to gate writes elsewhere and Executive must never inherit those.
        if (ProjectAccessPolicy.GlobalRoles.Contains(query.ActorRole) || Roles.IsOrgReadOnlyViewer(query.ActorRole))
            return ServiceResult<IReadOnlyList<ProjectDto>>.Ok(projects.Select(p => ProjectDto.From(p)).ToList());

        var memberProjectIds = await _projects.GetMemberProjectIdsAsync(query.ActorId, ct);
        var actor = await _engineers.GetByIdAsync(query.ActorId, ct);
        var actorTeam = actor?.TeamId is Guid actorTeamId ? await _teams.GetByIdAsync(actorTeamId, ct) : null;
        var isDeptHead = ProjectAccessPolicy.DepartmentHeadRoles.Contains(query.ActorRole);
        var isTeamLead = query.ActorRole == Roles.TeamLead;

        // A head with no team, or whose team has no department, is unscoped — sees everything
        // (mirrors ProjectAccessPolicy.IsProjectInActorDepartmentAsync).
        var deptHeadUnscoped = isDeptHead && actorTeam?.Department is null;

        var allTeams = (await _teams.ListAllAsync(ct)).ToDictionary(t => t.Id);

        // Team.TeamLeadId, not the lead's own Engineer.TeamId, is the source of truth for which
        // team a lead leads (mirrors ProjectAccessPolicy.GetLedTeamAsync). Not leading any team
        // is unscoped, same as a headless department head.
        var ledTeamId = isTeamLead ? allTeams.Values.FirstOrDefault(t => t.TeamLeadId == query.ActorId)?.Id : null;
        var teamLeadUnscoped = isTeamLead && ledTeamId is null;

        // A department head's followed projects — a deliberate opt-in, mirrors
        // ProjectAccessPolicy.CanAccessProjectAsync's follow check, so a project that opens
        // directly (200) is never shown here as canAccess:false. Deliberately NOT "any project one
        // of my engineers happens to be a member of" — that's incidental, not something the head
        // opted into.
        var followedProjectIds = isDeptHead
            ? (await _follows.GetFollowedProjectIdsAsync(query.ActorId, ct)).ToHashSet()
            : new HashSet<Guid>();

        // A team lead also reaches a project outside their own team if one of their engineers is
        // an explicit member of it — mirrors ProjectAccessPolicy.ProjectHasLedTeamMemberAsync.
        var teamMemberProjectIds = new HashSet<Guid>();
        if (isTeamLead && ledTeamId is Guid realLedTeamId)
        {
            var teamEngineerIds = (await _engineers.ListAllAsync(ct)).Where(e => e.TeamId == realLedTeamId);
            foreach (var engineer in teamEngineerIds)
                teamMemberProjectIds.UnionWith(await _projects.GetMemberProjectIdsAsync(engineer.Id, ct));
        }

        var dtos = projects.Select(p =>
        {
            var canAccess = memberProjectIds.Contains(p.Id)
                || (isDeptHead && (deptHeadUnscoped
                    || (p.OwnerTeamId is Guid ownerTeamId
                        && allTeams.TryGetValue(ownerTeamId, out var ownerTeam)
                        && ownerTeam.Department == actorTeam!.Department)
                    || followedProjectIds.Contains(p.Id)))
                || (isTeamLead && (teamLeadUnscoped || p.OwnerTeamId == ledTeamId || teamMemberProjectIds.Contains(p.Id)))
                || (!isDeptHead && !isTeamLead && p.OwnerTeamId is Guid teamId && teamId == actor?.TeamId);

            return ProjectDto.From(p, canAccess);
        }).ToList();

        return ServiceResult<IReadOnlyList<ProjectDto>>.Ok(dtos);
    }
}
