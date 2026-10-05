using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Performance.Queries;

/// <summary>Per-engineer performance for a team, optionally scoped to one project the team lead wants
/// to drill into. Access-gated to team_lead+ at the controller; CanAccessTeamAsync here additionally
/// scopes a department head to teams in their own department. Executive is read-only org-wide here via
/// its own explicit check rather than joining ProjectAccessPolicy.GlobalRoles, since that set is also
/// used to gate writes elsewhere and Executive must never inherit those (mirrors ListProjectsQuery).</summary>
public record GetTeamPerformanceQuery(
    Guid TeamId, Guid? ProjectId, Guid ActorId, string ActorRole, int Days = 30)
    : IRequest<ServiceResult<IReadOnlyList<PerformanceMetricsDto>>>;

public class GetTeamPerformanceHandler : IRequestHandler<GetTeamPerformanceQuery, ServiceResult<IReadOnlyList<PerformanceMetricsDto>>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly ICheckInRepository _checkIns;
    private readonly IProjectAccessPolicy _access;

    public GetTeamPerformanceHandler(
        IEngineerRepository engineers, ITaskRepository tasks, IEscalationEventRepository escalationEvents,
        ICheckInRepository checkIns, IProjectAccessPolicy access)
    {
        _engineers = engineers;
        _tasks = tasks;
        _escalationEvents = escalationEvents;
        _checkIns = checkIns;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<PerformanceMetricsDto>>> Handle(GetTeamPerformanceQuery query, CancellationToken ct)
    {
        if (query.ActorRole is not (Roles.Executive or Roles.HR) && !await _access.CanAccessTeamAsync(query.TeamId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<PerformanceMetricsDto>>.Fail("FORBIDDEN", "You do not have access to this team.");

        // Executive/HeadOfPmo/ProjectManager/Accountant can never carry a delivery workload (see
        // CheckInExpected, "everyone doing delivery work") — without this a non-delivery role on
        // the team would get a full metrics card of zeros/velocity-less stats.
        var deliveryRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        var roster = (await _engineers.ListWithWorkloadAsync(null, query.TeamId, ct))
            .Where(r => deliveryRoles.Contains(r.Engineer.Role))
            .ToList();

        var to = DateTime.UtcNow;
        var from = to.AddDays(-Math.Max(query.Days, 1));
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        var checkInCounts = await _checkIns.GetCheckInCountByDateRangeAsync(fromDate, toDate, ct);

        var results = new List<PerformanceMetricsDto>();
        foreach (var (engineer, _, _) in roster)
        {
            var stats = await _tasks.GetPerformanceStatsAsync(engineer.Id, from, to, query.ProjectId, ct);
            var escalated = await _escalationEvents.GetEscalatedTaskCountAsync(engineer.Id, from, to, query.ProjectId, ct);
            var checkInCount = checkInCounts.GetValueOrDefault(engineer.Id, 0);

            results.Add(PerformanceMetricsCalculator.Compute(
                engineer.Id, engineer.Name, engineer.BaselinePoints, engineer.BaselineCycleDays, stats, escalated, checkInCount, fromDate, toDate));
        }

        return ServiceResult<IReadOnlyList<PerformanceMetricsDto>>.Ok(results);
    }
}
