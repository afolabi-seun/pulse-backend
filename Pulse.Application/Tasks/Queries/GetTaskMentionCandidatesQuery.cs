using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Engineers;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

/// <summary>Backs the @mention candidate list on a task's comments and blocker reason. Task-scoped
/// rather than project-scoped, because the candidate set includes the task's own creator — always
/// mentionable on their own task even when their role/team carries no standing project access
/// (e.g. a PMO engineer who filed a task for a team they aren't part of) — which a project-level
/// query has no way to know. See ProjectAccessPolicy.GetAccessibleEngineerIdsAsync for everyone
/// else in the set.</summary>
public record GetTaskMentionCandidatesQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class GetTaskMentionCandidatesHandler : IRequestHandler<GetTaskMentionCandidatesQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;
    private readonly IEngineerRepository _engineers;

    public GetTaskMentionCandidatesHandler(ITaskRepository tasks, IProjectAccessPolicy access, IEngineerRepository engineers)
    {
        _tasks = tasks;
        _access = access;
        _engineers = engineers;
    }

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(GetTaskMentionCandidatesQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
            return ServiceResult<IReadOnlyList<EngineerDto>>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        // Same org-wide read-only viewer bypass GetTaskQuery uses — the task drawer's comment box
        // loads these candidates for any viewer who can open the task, even though they can't post.
        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || await _access.CanViewTaskAsync(query.TaskId, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<IReadOnlyList<EngineerDto>>.Fail("FORBIDDEN", "You do not have access to this task.");

        var ids = (await _access.GetAccessibleEngineerIdsAsync(task.ProjectId, ct)).ToHashSet();
        if (task.CreatedById is Guid creatorId)
            ids.Add(creatorId);

        var engineers = await _engineers.GetByIdsAsync(ids.ToList(), ct);

        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(
            engineers.Select(EngineerDto.From).OrderBy(e => e.Name).ToList());
    }
}
