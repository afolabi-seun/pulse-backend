using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Sprints.Queries;

public record GetSprintVelocityQuery(Guid SprintId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<SprintVelocityDto>>;

public class GetSprintVelocityHandler : IRequestHandler<GetSprintVelocityQuery, ServiceResult<SprintVelocityDto>>
{
    private readonly ISprintRepository _sprints;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public GetSprintVelocityHandler(ISprintRepository sprints, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<SprintVelocityDto>> Handle(GetSprintVelocityQuery query, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(query.SprintId, ct);
        if (sprint is null)
            return ServiceResult<SprintVelocityDto>.Fail("NOT_FOUND", $"Sprint '{query.SprintId}' not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessSprintAsync(sprint.Id, sprint.TeamId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<SprintVelocityDto>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        var tasks = await _tasks.GetBySprintAsync(query.SprintId, ct);

        // QA sub-tasks carry their own copy of the parent's points and are their own row in the
        // sprint — exclude them from both the point sums and the task counts, or a task that went
        // through QA gets counted twice (once as itself, once as its "[QA] ..." review card). This
        // used to match the board on the theory that sub-tasks are real board cards, but the board
        // hides a parent once it's sent to QA and shows only the sub-task in its place — so the
        // unfiltered count was actually higher than what the board ever displays, not equal to it.
        var scopeTasks = tasks.ExcludingQaSubtasks().ToList();
        var planned    = scopeTasks.Sum(t => t.Points);
        var delivered  = scopeTasks.Where(t => t.Status == Domain.Tasks.TaskStatus.Done).Sum(t => t.Points);
        var done       = scopeTasks.Count(t => t.Status == Domain.Tasks.TaskStatus.Done);

        return ServiceResult<SprintVelocityDto>.Ok(
            new SprintVelocityDto(planned, delivered, scopeTasks.Count, done));
    }
}
