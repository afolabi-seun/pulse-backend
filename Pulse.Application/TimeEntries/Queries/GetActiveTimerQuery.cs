using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record GetActiveTimerQuery(Guid EngineerId) : IRequest<ServiceResult<ActiveTimerDto?>>;

public class GetActiveTimerHandler : IRequestHandler<GetActiveTimerQuery, ServiceResult<ActiveTimerDto?>>
{
    private readonly IActiveTimerRepository _timers;
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;

    public GetActiveTimerHandler(IActiveTimerRepository timers, ITaskRepository tasks, ISubtaskRepository subtasks)
    {
        _timers = timers;
        _tasks = tasks;
        _subtasks = subtasks;
    }

    public async Task<ServiceResult<ActiveTimerDto?>> Handle(GetActiveTimerQuery query, CancellationToken ct)
    {
        var timer = await _timers.GetByEngineerAsync(query.EngineerId, ct);
        if (timer is null)
            return ServiceResult<ActiveTimerDto?>.Ok(null);

        string? taskTitle = null;
        if (timer.TaskId.HasValue)
        {
            var task = await _tasks.GetByIdAsync(timer.TaskId.Value, ct);
            taskTitle = task?.Title;
        }

        string? subtaskTitle = null;
        if (timer.SubtaskId.HasValue)
        {
            var subtask = await _subtasks.GetByIdAsync(timer.SubtaskId.Value, ct);
            subtaskTitle = subtask?.Title;
        }

        return ServiceResult<ActiveTimerDto?>.Ok(ActiveTimerDto.From(timer, taskTitle, subtaskTitle));
    }
}
