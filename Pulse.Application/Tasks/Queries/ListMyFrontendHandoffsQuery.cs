using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.Application.Tasks.Queries;

/// <summary>A backend engineer's own view of the tasks they've handed off to Frontend — the ones
/// that dropped off their radar the moment AssigneeId moved on, even though they finished real
/// work on them. Self-scoped like <see cref="GetQaQueueQuery"/>: BackendAssigneeId is only ever
/// set by <see cref="PulseTask.HandOffToFrontend"/>/<see cref="PulseTask.HandOffToBackend"/>,
/// so seeing your own ID there always means you legitimately worked on it.</summary>
public record ListMyFrontendHandoffsQuery(Guid ActorId) : IRequest<ServiceResult<IReadOnlyList<TaskDto>>>;

public class ListMyFrontendHandoffsHandler : IRequestHandler<ListMyFrontendHandoffsQuery, ServiceResult<IReadOnlyList<TaskDto>>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectRepository _projects;

    public ListMyFrontendHandoffsHandler(ITaskRepository tasks, IEngineerRepository engineers, IProjectRepository projects)
    {
        _tasks = tasks;
        _engineers = engineers;
        _projects = projects;
    }

    public async Task<ServiceResult<IReadOnlyList<TaskDto>>> Handle(ListMyFrontendHandoffsQuery query, CancellationToken ct)
    {
        var items = await _tasks.GetHandedOffByAsync(query.ActorId, ct);

        var projectIds = items.Select(t => t.ProjectId).Distinct().ToList();
        var projectNames = projectIds.Count > 0 ? await _projects.GetNamesByIdsAsync(projectIds, ct) : new Dictionary<Guid, string>();
        var projectCodes = projectIds.Count > 0 ? await _projects.GetCodesByIdsAsync(projectIds, ct) : new Dictionary<Guid, string>();

        var assigneeIds = items.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value).Distinct().ToList();
        var engineerNames = assigneeIds.Count > 0
            ? (await _engineers.GetByIdsAsync(assigneeIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var dtos = items
            .OrderBy(t => t.Status == DomainTaskStatus.Done ? 1 : 0)
            .ThenBy(t => t.DueDate)
            .Select(t => TaskDto.From(t,
                projectNames.TryGetValue(t.ProjectId, out var pName) ? pName : null,
                t.AssigneeId.HasValue && engineerNames.TryGetValue(t.AssigneeId.Value, out var aName) ? aName : null,
                projectCode: projectCodes.TryGetValue(t.ProjectId, out var pCode) ? pCode : null))
            .ToList();

        return ServiceResult<IReadOnlyList<TaskDto>>.Ok(dtos);
    }
}
