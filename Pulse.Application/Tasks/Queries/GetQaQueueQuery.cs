using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

/// <summary>
/// A QA engineer's open review queue: QA sub-tasks assigned to them, across every project —
/// including ones they aren't a member of (auto-assignment doesn't grant membership).
/// Mirrors the self-scoped /reports/me and /vitals/me pattern rather than going through the
/// project-access model, since a QA task's assignee is always allowed to see it.
/// </summary>
public record GetQaQueueQuery(Guid ActorId) : IRequest<ServiceResult<IReadOnlyList<TaskDto>>>;

public class GetQaQueueHandler : IRequestHandler<GetQaQueueQuery, ServiceResult<IReadOnlyList<TaskDto>>>
{
    private readonly ITaskRepository _tasks;

    public GetQaQueueHandler(ITaskRepository tasks) => _tasks = tasks;

    public async Task<ServiceResult<IReadOnlyList<TaskDto>>> Handle(GetQaQueueQuery query, CancellationToken ct)
    {
        var assigned = await _tasks.GetActiveByAssigneeAsync(query.ActorId, ct);

        var queue = assigned
            .Where(t => t.ParentTaskId.HasValue)
            .OrderBy(t => t.DueDate)
            .Select(t => TaskDto.From(t))
            .ToList();

        return ServiceResult<IReadOnlyList<TaskDto>>.Ok(queue);
    }
}
