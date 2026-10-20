using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Performance.Queries;

public record GetMyPerformanceQuery(Guid EngineerId, int Days = 30) : IRequest<ServiceResult<PerformanceMetricsDto>>;

public class GetMyPerformanceHandler : IRequestHandler<GetMyPerformanceQuery, ServiceResult<PerformanceMetricsDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly ICheckInRepository _checkIns;
    private readonly OverworkThresholds _thresholds;

    public GetMyPerformanceHandler(
        IEngineerRepository engineers, ITaskRepository tasks,
        IEscalationEventRepository escalationEvents, ICheckInRepository checkIns, OverworkThresholds thresholds)
    {
        _engineers = engineers;
        _tasks = tasks;
        _escalationEvents = escalationEvents;
        _checkIns = checkIns;
        _thresholds = thresholds;
    }

    public async Task<ServiceResult<PerformanceMetricsDto>> Handle(GetMyPerformanceQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<PerformanceMetricsDto>.Fail("NOT_FOUND", $"Engineer '{query.EngineerId}' not found.");

        var to = DateTime.UtcNow;
        var from = to.AddDays(-Math.Max(query.Days, 1));
        // The window starts no earlier than the reporting start date: what is expected of someone (points, check-in days) and the check-ins counted
        // are measured over the days that are actually reported on, not the full 30 or 90 days back that reach into earlier, excluded records.
        var fromDate = _thresholds.FloorDate(DateOnly.FromDateTime(from));
        var toDate = DateOnly.FromDateTime(to);

        var stats = await _tasks.GetPerformanceStatsAsync(engineer.Id, from, to, projectId: null, ct);
        var escalated = await _escalationEvents.GetEscalatedTaskCountAsync(engineer.Id, from, to, projectId: null, ct);
        var checkInCounts = await _checkIns.GetCheckInCountByDateRangeAsync(fromDate, toDate, ct);
        var checkInCount = checkInCounts.GetValueOrDefault(engineer.Id, 0);

        var dto = PerformanceMetricsCalculator.Compute(
            engineer.Id, engineer.Name, engineer.BaselinePoints, engineer.BaselineCycleDays, stats, escalated, checkInCount, fromDate, toDate);

        return ServiceResult<PerformanceMetricsDto>.Ok(dto);
    }
}
