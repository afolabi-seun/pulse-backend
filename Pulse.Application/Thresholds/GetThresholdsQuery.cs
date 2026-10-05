using Pulse.Application.Common;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Thresholds;

public record GetThresholdsQuery : IRequest<ServiceResult<ThresholdsDto>>;

/// <summary>Returns the current overwork and escalation threshold values from the live singleton.</summary>
public class GetThresholdsHandler : IRequestHandler<GetThresholdsQuery, ServiceResult<ThresholdsDto>>
{
    private readonly OverworkThresholds _thresholds;

    public GetThresholdsHandler(OverworkThresholds thresholds) => _thresholds = thresholds;

    public Task<ServiceResult<ThresholdsDto>> Handle(GetThresholdsQuery query, CancellationToken ct) =>
        Task.FromResult(ServiceResult<ThresholdsDto>.Ok(new ThresholdsDto(
            _thresholds.LoadVsBaselineRatio,
            _thresholds.MaxConcurrentTasks,
            _thresholds.StaleCycleMultiplier,
            _thresholds.SignalsRequiredToFlag,
            _thresholds.EscalationT3Days,
            _thresholds.EscalationT3ElapsedPct,
            _thresholds.EscalationT1Days,
            _thresholds.EscalationT1ElapsedPct,
            _thresholds.EscalationT3MinHours,
            _thresholds.EscalationT1MinHours,
            _thresholds.QaLeadTimeDays,
            _thresholds.PointScale.Select(e => new PointScaleEntryDto(e.Value, e.Label, e.TimeGuide)).ToList(),
            _thresholds.PriorityScale.Select(e => new PriorityScaleEntryDto(e.Value, e.Label, e.Criteria)).ToList())));
}
