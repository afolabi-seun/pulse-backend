using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Active engineers who actually work on a given project — its owner team's roster plus
/// anyone explicitly added as a project member — regardless of the caller's own department.
/// Backs the task Assignee picker for a normal (non-QA-subtask) task: ListEngineersQuery is
/// scoped to the caller's own department, so a department head managing a project outside their
/// department (via cross-department follow/membership, which CanAccessProjectAsync already
/// allows) would otherwise see none of the people who'd actually do the work — the same
/// reasoning GetLoanCandidatesQuery/GetQaCandidatesQuery already apply to their own pickers.</summary>
public record GetProjectAssignableEngineersQuery(Guid ProjectId, Guid ActorId, string ActorRole)
    : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class GetProjectAssignableEngineersHandler : IRequestHandler<GetProjectAssignableEngineersQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetProjectAssignableEngineersHandler(IProjectRepository projects, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _projects = projects;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(GetProjectAssignableEngineersQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<EngineerDto>>.Fail("NOT_FOUND", "Project not found.");

        if (query.ActorRole != Roles.Executive && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<EngineerDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var candidates = (await ProjectRoster.ListActiveAsync(project, _projects, _engineers, ct))
            .Select(EngineerDto.From)
            .ToList();

        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(candidates);
    }
}

/// <summary>Who actually works on a project: its owner team's roster plus explicit project members.</summary>
public static class ProjectRoster
{
    public static async Task<IReadOnlyList<Engineer>> ListActiveAsync(
        Domain.Projects.Project project, IProjectRepository projects, IEngineerRepository engineers, CancellationToken ct)
    {
        var active = await engineers.ListActiveAsync(ct);

        var candidateIds = (await projects.ListMembersAsync(project.Id, ct))
            .Select(m => m.EngineerId)
            .ToHashSet();
        if (project.OwnerTeamId is Guid ownerTeamId)
            foreach (var e in active.Where(e => e.TeamId == ownerTeamId))
                candidateIds.Add(e.Id);

        return active
            .Where(e => candidateIds.Contains(e.Id))
            .OrderBy(e => e.Name)
            .ToList();
    }
}
