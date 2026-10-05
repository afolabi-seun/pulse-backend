using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Sprints.Queries;

public record BurndownPointDto(string Date, int Remaining, int Ideal);

public record GetSprintBurndownQuery(Guid SprintId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<BurndownPointDto>>>;

public class GetSprintBurndownHandler : IRequestHandler<GetSprintBurndownQuery, ServiceResult<IReadOnlyList<BurndownPointDto>>>
{
    private readonly ISprintRepository _sprints;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public GetSprintBurndownHandler(ISprintRepository sprints, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _tasks   = tasks;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<BurndownPointDto>>> Handle(GetSprintBurndownQuery query, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(query.SprintId, ct);
        if (sprint is null)
            return ServiceResult<IReadOnlyList<BurndownPointDto>>.Fail("NOT_FOUND", $"Sprint '{query.SprintId}' not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessSprintAsync(sprint.Id, sprint.TeamId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<BurndownPointDto>>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        // QA sub-tasks carry their own copy of the parent's points — excluded so a task that went
        // through QA doesn't inflate the total scope the burndown line is drawn against. Paused
        // tasks stay in scope — they're still committed work, just on hold — matching how
        // GetSprintVelocityQuery's "Planned pts" and the PMO report treat them.
        var tasks = (await _tasks.GetBySprintAsync(query.SprintId, ct))
            .ExcludingQaSubtasks()
            .ToList();
        var totalPoints = tasks.Sum(t => t.Points);
        if (totalPoints == 0)
            return ServiceResult<IReadOnlyList<BurndownPointDto>>.Ok([]);

        var completions = await _tasks.GetBurndownDataAsync(query.SprintId, ct);

        // Cumulative points done by date
        var pointsByDate = new Dictionary<DateOnly, int>();
        foreach (var (date, pts) in completions)
        {
            pointsByDate.TryGetValue(date, out var existing);
            pointsByDate[date] = existing + pts;
        }

        var start    = sprint.StartDate;
        var end      = sprint.EndDate;
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);
        var lastDay  = today < end ? today : end;
        var totalDays = (end.ToDateTime(TimeOnly.MinValue) - start.ToDateTime(TimeOnly.MinValue)).TotalDays;

        var result = new List<BurndownPointDto>();
        var cumDone = 0;

        for (var day = start; day <= lastDay; day = day.AddDays(1))
        {
            if (pointsByDate.TryGetValue(day, out var done)) cumDone += done;

            var remaining = Math.Max(totalPoints - cumDone, 0);

            var elapsed = (day.ToDateTime(TimeOnly.MinValue) - start.ToDateTime(TimeOnly.MinValue)).TotalDays;
            var ideal   = totalDays > 0 ? (int)Math.Round(totalPoints * (1 - elapsed / totalDays)) : 0;

            result.Add(new BurndownPointDto(day.ToString("yyyy-MM-dd"), remaining, Math.Max(ideal, 0)));
        }

        return ServiceResult<IReadOnlyList<BurndownPointDto>>.Ok(result);
    }
}
