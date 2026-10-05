using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record TaskTimeSummaryDto(Guid TaskId, decimal TotalHoursLogged, decimal? ExpectedHours);

public record GetTaskTimeSummaryQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<TaskTimeSummaryDto>>;

public class GetTaskTimeSummaryHandler : IRequestHandler<GetTaskTimeSummaryQuery, ServiceResult<TaskTimeSummaryDto>>
{
    // A standard workday length, used only to convert the existing points-derived day estimate
    // into hours comparable against logged time — not a claim about anyone's actual schedule.
    private const decimal WorkdayHours = 8m;

    private readonly ITimeEntryRepository _timeEntries;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetTaskTimeSummaryHandler(
        ITimeEntryRepository timeEntries, ITaskRepository tasks, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _timeEntries = timeEntries;
        _tasks = tasks;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<TaskTimeSummaryDto>> Handle(GetTaskTimeSummaryQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskTimeSummaryDto>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanViewTaskAsync(query.TaskId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<TaskTimeSummaryDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var totalHours = await _timeEntries.GetTotalHoursByTaskAsync(query.TaskId, ct);

        decimal? expectedHours = null;
        if (task.AssigneeId is Guid assigneeId)
        {
            var assignee = await _engineers.GetByIdAsync(assigneeId, ct);
            if (assignee is { BaselinePoints: > 0 })
                expectedHours = task.Points * (decimal)assignee.BaselineCycleDays / assignee.BaselinePoints * WorkdayHours;
        }

        return ServiceResult<TaskTimeSummaryDto>.Ok(new TaskTimeSummaryDto(query.TaskId, totalHours, expectedHours));
    }
}
