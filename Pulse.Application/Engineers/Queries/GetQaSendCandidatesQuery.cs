using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Slim roster entry for the "Send to QA" reviewer picker.</summary>
public record QaSendCandidateDto(Guid Id, string Name, string? Discipline, bool OnProject);

public record QaSendCandidatesDto(IReadOnlyList<QaSendCandidateDto> Candidates, Guid? RecommendedEngineerId);

/// <summary>Active QA engineers org-wide, scoped to a specific task and open to its own assignee —
/// not just PM-or-above, mirroring GetHandoffCandidatesQuery's reasoning: the assignee is exactly
/// who triggers SendToQaCommand, so a plain engineer sending their own work to QA needs to see this
/// list too, not just department heads. RecommendedEngineerId mirrors what SendToQaCommand would
/// auto-pick if the caller submits no override, via the same QaAutoAssignment.Recommend so the
/// suggested default and the actual fallback behavior can never drift apart.</summary>
public record GetQaSendCandidatesQuery(Guid TaskId, Guid ActorId, string ActorRole)
    : IRequest<ServiceResult<QaSendCandidatesDto>>;

public class GetQaSendCandidatesHandler : IRequestHandler<GetQaSendCandidatesQuery, ServiceResult<QaSendCandidatesDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetQaSendCandidatesHandler(ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<QaSendCandidatesDto>> Handle(GetQaSendCandidatesQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
            return ServiceResult<QaSendCandidatesDto>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        if (task.AssigneeId != query.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<QaSendCandidatesDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var memberIds = (await _projects.ListMembersAsync(task.ProjectId, ct)).Select(m => m.EngineerId).ToHashSet();

        var allActiveQa = (await _engineers.ListActiveAsync(ct)).Where(e => e.IsQa).ToList();
        var onProjectQa = allActiveQa.Where(e => memberIds.Contains(e.Id)).ToList();

        var recommended = QaAutoAssignment.Recommend(onProjectQa, allActiveQa, task.Discipline);

        var candidates = allActiveQa
            .Select(e => new QaSendCandidateDto(
                e.Id, e.Name,
                e.Discipline.HasValue ? Camel(e.Discipline.Value.ToString()) : null,
                memberIds.Contains(e.Id)))
            .OrderByDescending(c => c.OnProject)
            .ThenBy(c => c.Name)
            .ToList();

        return ServiceResult<QaSendCandidatesDto>.Ok(new QaSendCandidatesDto(candidates, recommended));
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
