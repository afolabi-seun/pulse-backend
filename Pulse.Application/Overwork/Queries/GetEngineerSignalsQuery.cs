using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Overwork.Queries;

public record GetEngineerSignalsQuery(Guid EngineerId) : IRequest<ServiceResult<OverworkSignalsDto>>;

public class GetEngineerSignalsHandler : IRequestHandler<GetEngineerSignalsQuery, ServiceResult<OverworkSignalsDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly ITeamRepository _teams;
    private readonly IDepartmentThresholdRepository _departmentThresholds;
    private readonly OverworkSignalsCalculator _calculator;

    public GetEngineerSignalsHandler(
        IEngineerRepository engineers,
        IOverworkOverrideRepository overrides,
        ITeamRepository teams,
        IDepartmentThresholdRepository departmentThresholds,
        OverworkSignalsCalculator calculator)
    {
        _engineers = engineers;
        _overrides = overrides;
        _teams = teams;
        _departmentThresholds = departmentThresholds;
        _calculator = calculator;
    }

    public async Task<ServiceResult<OverworkSignalsDto>> Handle(GetEngineerSignalsQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<OverworkSignalsDto>.Fail("NOT_FOUND", $"Engineer '{query.EngineerId}' not found.");

        var (_, activeTasks) = await _engineers.GetWithActiveTasksAsync(query.EngineerId, ct);
        var @override = await _overrides.GetActiveAsync(query.EngineerId, ct);

        DepartmentThresholdOverride? departmentOverride = null;
        if (engineer.TeamId is Guid teamId)
        {
            var team = await _teams.GetByIdAsync(teamId, ct);
            if (team?.Department is string department)
                departmentOverride = await _departmentThresholds.GetByDepartmentAsync(department, ct);
        }

        var (signals, isOverworked) = _calculator.Compute(engineer, activeTasks, @override, departmentOverride);

        return ServiceResult<OverworkSignalsDto>.Ok(
            OverworkSignalsDto.From(signals, isOverworked, @override?.IsActive == true));
    }
}
