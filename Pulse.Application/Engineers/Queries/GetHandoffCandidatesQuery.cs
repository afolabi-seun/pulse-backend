using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Deliberately slim — a hand-off picker only needs who to offer, so unlike EngineerDto it
/// carries no email, baseline or workload.</summary>
public record HandoffCandidateDto(Guid Id, string Name, string? Discipline, bool OnProject);

/// <summary>Every active engineer org-wide with the given discipline — the valid targets for a
/// Backend → Frontend (or Frontend → Backend) hand-off. The hand-off itself adds the target to the
/// project (HandOffToFrontendCommand/HandOffToBackendCommand), so being on it already is not a
/// precondition; <see cref="HandoffCandidateDto.OnProject"/> just lets the picker put the project's own
/// people first. Open to the task's own assignee, not just
/// PMO and above: the assignee is exactly who performs the hand-off (HandOffToFrontendCommand
/// authorises them directly), but the general project-assignable roster is PM-only, so a backend
/// engineer previously saw an empty picker.</summary>
public record GetHandoffCandidatesQuery(Guid TaskId, string Discipline, Guid ActorId, string ActorRole)
    : IRequest<ServiceResult<IReadOnlyList<HandoffCandidateDto>>>;

public class GetHandoffCandidatesHandler : IRequestHandler<GetHandoffCandidatesQuery, ServiceResult<IReadOnlyList<HandoffCandidateDto>>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetHandoffCandidatesHandler(ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<HandoffCandidateDto>>> Handle(GetHandoffCandidatesQuery query, CancellationToken ct)
    {
        if (!Enum.TryParse<Domain.Tasks.Discipline>(query.Discipline, ignoreCase: true, out var discipline)
            || discipline is not (Domain.Tasks.Discipline.Frontend or Domain.Tasks.Discipline.Backend))
            return ServiceResult<IReadOnlyList<HandoffCandidateDto>>.Fail("VALIDATION_ERROR", "Discipline must be 'frontend' or 'backend'.");

        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
            return ServiceResult<IReadOnlyList<HandoffCandidateDto>>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        // Same rule as HandOffToFrontendCommand/HandOffToBackendCommand: the task's assignee, or
        // anyone with access to its project.
        if (task.AssigneeId != query.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<HandoffCandidateDto>>.Fail("FORBIDDEN", "You do not have access to this task.");

        var project = await _projects.GetByIdAsync(task.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<HandoffCandidateDto>>.Fail("NOT_FOUND", "Project not found.");

        var onProject = (await ProjectRoster.ListActiveAsync(project, _projects, _engineers, ct))
            .Select(e => e.Id)
            .ToHashSet();

        var disciplineLabel = discipline == Domain.Tasks.Discipline.Frontend ? "frontend" : "backend";
        var candidates = (await _engineers.ListActiveAsync(ct))
            .Where(e => e.Discipline == discipline)
            .Select(e => new HandoffCandidateDto(e.Id, e.Name, disciplineLabel, onProject.Contains(e.Id)))
            .OrderByDescending(c => c.OnProject)
            .ThenBy(c => c.Name)
            .ToList();

        return ServiceResult<IReadOnlyList<HandoffCandidateDto>>.Ok(candidates);
    }
}
