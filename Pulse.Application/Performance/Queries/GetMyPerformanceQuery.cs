using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Performance.Queries;

public record GetMyPerformanceQuery(Guid EngineerId, int Days = 30) : IRequest<ServiceResult<PerformanceMetricsDto>>;

public class GetMyPerformanceHandler : IRequestHandler<GetMyPerformanceQuery, ServiceResult<PerformanceMetricsDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly ICheckInRepository _checkIns;

    public GetMyPerformanceHandler(
        IEngineerRepository engineers, ITaskRepository tasks,
        IEscalationEventRepository escalationEvents, ICheckInRepository checkIns)
    {
        _engineers = engineers;
        _tasks = tasks;
        _escalationEvents = escalationEvents;
        _checkIns = checkIns;
    }

    public async Task<ServiceResult<PerformanceMetricsDto>> Handle(GetMyPerformanceQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<PerformanceMetricsDto>.Fail("NOT_FOUND", $"Engineer '{query.EngineerId}' not found.");

        var to = DateTime.UtcNow;
        var from = to.AddDays(-Math.Max(query.Days, 1));
        var fromDate = DateOnly.FromDateTime(from);
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
