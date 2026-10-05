using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Performance.Queries;

/// <summary>Project-level aggregate across its members — no per-engineer breakdown and no check-in
/// consistency (check-ins aren't cleanly divisible by project, and this view is about project output,
/// not individual habits).</summary>
public record ProjectPerformanceDto(
    Guid ProjectId,
    string ProjectName,
    DateOnly From,
    DateOnly To,
    int EngineerCount,
    int DeliveredPoints,
    int TasksCompleted,
    // Of TasksCompleted, how many actually had a due date — the real denominator for OnTimeRate.
    int TasksWithDueDate,
    int TasksCompletedOnTime,
    double? OnTimeRate,
    double? AvgCycleTimeDays,
    int TasksSentToQa,
    int TasksQaRejected,
    double? QaRejectRate,
    int EscalatedTaskCount);

public record GetProjectPerformanceQuery(Guid ProjectId, Guid ActorId, string ActorRole, int Days = 30)
    : IRequest<ServiceResult<ProjectPerformanceDto>>;

public class GetProjectPerformanceHandler : IRequestHandler<GetProjectPerformanceQuery, ServiceResult<ProjectPerformanceDto>>
{
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly IProjectAccessPolicy _access;

    public GetProjectPerformanceHandler(
        IProjectRepository projects, ITaskRepository tasks,
        IEscalationEventRepository escalationEvents, IProjectAccessPolicy access)
    {
        _projects = projects;
        _tasks = tasks;
        _escalationEvents = escalationEvents;
        _access = access;
    }

    public async Task<ServiceResult<ProjectPerformanceDto>> Handle(GetProjectPerformanceQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<ProjectPerformanceDto>.Fail("NOT_FOUND", $"Project '{query.ProjectId}' not found.");

        // Executive is read-only org-wide via its own explicit check rather than joining
        // ProjectAccessPolicy.GlobalRoles, since that set also gates writes elsewhere and
        // Executive must never inherit those (mirrors ListProjectsQuery).
        if (query.ActorRole is not (Roles.Executive or Roles.HR) && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<ProjectPerformanceDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var members = await _projects.ListMembersAsync(project.Id, ct);

        var to = DateTime.UtcNow;
        var from = to.AddDays(-Math.Max(query.Days, 1));
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        var deliveredPoints = 0;
        var tasksCompleted = 0;
        var tasksWithDueDate = 0;
        var tasksOnTime = 0;
        var sentToQa = 0;
        var rejected = 0;
        var escalated = 0;
        // Weighted by each engineer's own task count rather than averaging their per-engineer
        // averages — an engineer with 1 completed task previously counted exactly as much as one
        // with 30, so a single low-volume outlier could swing the whole project's figure. Since
        // AvgCycleTimeDays is itself an average over exactly TasksCompleted tasks, weightedSum/count
        // reconstructs the true task-weighted mean across every member's tasks combined.
        var cycleTimeWeightedSum = 0.0;
        var cycleTimeTaskCount = 0;

        foreach (var member in members)
        {
            var stats = await _tasks.GetPerformanceStatsAsync(member.EngineerId, from, to, project.Id, ct);
            deliveredPoints += stats.DeliveredPoints;
            tasksCompleted += stats.TasksCompleted;
            tasksWithDueDate += stats.TasksWithDueDate;
            tasksOnTime += stats.TasksCompletedOnTime;
            sentToQa += stats.TasksSentToQa;
            rejected += stats.TasksQaRejected;
            if (stats.AvgCycleTimeDays.HasValue)
            {
                cycleTimeWeightedSum += stats.AvgCycleTimeDays.Value * stats.TasksCompleted;
                cycleTimeTaskCount += stats.TasksCompleted;
            }
            escalated += await _escalationEvents.GetEscalatedTaskCountAsync(member.EngineerId, from, to, project.Id, ct);
        }

        // Denominator is tasks that actually had a due date — see TaskPerformanceStats' own doc
        // comment for why a task with none is excluded rather than counted as trivially on-time.
        double? onTimeRate = tasksWithDueDate > 0 ? (double)tasksOnTime / tasksWithDueDate : null;
        double? qaRejectRate = sentToQa > 0 ? (double)rejected / sentToQa : null;
        double? avgCycleTimeDays = cycleTimeTaskCount > 0 ? cycleTimeWeightedSum / cycleTimeTaskCount : null;

        var dto = new ProjectPerformanceDto(
            project.Id, project.Name, fromDate, toDate, members.Count,
            deliveredPoints, tasksCompleted, tasksWithDueDate, tasksOnTime, onTimeRate, avgCycleTimeDays,
            sentToQa, rejected, qaRejectRate, escalated);

        return ServiceResult<ProjectPerformanceDto>.Ok(dto);
    }
}
