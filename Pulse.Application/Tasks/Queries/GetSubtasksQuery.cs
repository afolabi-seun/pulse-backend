using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

public record GetSubtasksQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<SubtaskDto>>>;

public class GetSubtasksHandler : IRequestHandler<GetSubtasksQuery, ServiceResult<IReadOnlyList<SubtaskDto>>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetSubtasksHandler(ITaskRepository tasks, ISubtaskRepository subtasks, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _subtasks = subtasks;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<SubtaskDto>>> Handle(GetSubtasksQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
            return ServiceResult<IReadOnlyList<SubtaskDto>>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        var items = await _subtasks.GetByTaskIdAsync(query.TaskId, ct);

        // A subtask loaned to someone outside the task's own project needs its own access path,
        // same idea as ProjectAccessPolicy.CanAccessTaskAsync's unassigned-QA-subtask carve-out.
        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || items.Any(s => s.AssigneeId == query.ActorId)
            || await _access.CanViewTaskAsync(query.TaskId, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<IReadOnlyList<SubtaskDto>>.Fail("FORBIDDEN", "You do not have access to this task.");

        var assigneeIds = items.Where(s => s.AssigneeId.HasValue).Select(s => s.AssigneeId!.Value).Distinct().ToList();
        var assigneeNames = assigneeIds.Count > 0
            ? (await _engineers.GetByIdsAsync(assigneeIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        return ServiceResult<IReadOnlyList<SubtaskDto>>.Ok(
            items.Select(s => SubtaskDto.From(s, s.AssigneeId.HasValue && assigneeNames.TryGetValue(s.AssigneeId.Value, out var n) ? n : null)).ToList());
    }
}
