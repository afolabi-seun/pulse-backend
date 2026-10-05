using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Thresholds;

public record UpdateThresholdsCommand(
    double? LoadVsBaselineRatio,
    int? MaxConcurrentTasks,
    double? StaleCycleMultiplier,
    int? SignalsRequiredToFlag,
    double? EscalationT3Days,
    double? EscalationT3ElapsedPct,
    double? EscalationT1Days,
    double? EscalationT1ElapsedPct,
    double? EscalationT3MinHours,
    double? EscalationT1MinHours,
    int? QaLeadTimeDays,
    IReadOnlyList<PointScaleEntryDto>? PointScale,
    IReadOnlyList<PriorityScaleEntryDto>? PriorityScale,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<ThresholdsDto>>;

/// <summary>
/// Persists threshold changes to DB and updates the in-memory singleton so existing background jobs
/// pick up the new values without a restart.
/// </summary>
public class UpdateThresholdsHandler : IRequestHandler<UpdateThresholdsCommand, ServiceResult<ThresholdsDto>>
{
    private readonly IThresholdRepository _repo;
    private readonly OverworkThresholds _thresholds;
    private readonly IAuditLogRepository _audit;

    public UpdateThresholdsHandler(IThresholdRepository repo, OverworkThresholds thresholds, IAuditLogRepository audit)
    {
        _repo = repo;
        _thresholds = thresholds;
        _audit = audit;
    }

    public async Task<ServiceResult<ThresholdsDto>> Handle(UpdateThresholdsCommand cmd, CancellationToken ct)
    {
        if (cmd.LoadVsBaselineRatio.HasValue)
        {
            _thresholds.LoadVsBaselineRatio = cmd.LoadVsBaselineRatio.Value;
            await _repo.SetAsync("LoadVsBaselineRatio", cmd.LoadVsBaselineRatio.Value.ToString("G"), ct);
        }

        if (cmd.MaxConcurrentTasks.HasValue)
        {
            _thresholds.MaxConcurrentTasks = cmd.MaxConcurrentTasks.Value;
            await _repo.SetAsync("MaxConcurrentTasks", cmd.MaxConcurrentTasks.Value.ToString(), ct);
        }

        if (cmd.StaleCycleMultiplier.HasValue)
        {
            _thresholds.StaleCycleMultiplier = cmd.StaleCycleMultiplier.Value;
            await _repo.SetAsync("StaleCycleMultiplier", cmd.StaleCycleMultiplier.Value.ToString("G"), ct);
        }

        if (cmd.SignalsRequiredToFlag.HasValue)
        {
            _thresholds.SignalsRequiredToFlag = cmd.SignalsRequiredToFlag.Value;
            await _repo.SetAsync("SignalsRequiredToFlag", cmd.SignalsRequiredToFlag.Value.ToString(), ct);
        }

        if (cmd.EscalationT3Days.HasValue)
        {
            _thresholds.EscalationT3Days = cmd.EscalationT3Days.Value;
            await _repo.SetAsync("EscalationT3Days", cmd.EscalationT3Days.Value.ToString("G"), ct);
        }

        if (cmd.EscalationT3ElapsedPct.HasValue)
        {
            _thresholds.EscalationT3ElapsedPct = cmd.EscalationT3ElapsedPct.Value;
            await _repo.SetAsync("EscalationT3ElapsedPct", cmd.EscalationT3ElapsedPct.Value.ToString("G"), ct);
        }

        if (cmd.EscalationT1Days.HasValue)
        {
            _thresholds.EscalationT1Days = cmd.EscalationT1Days.Value;
            await _repo.SetAsync("EscalationT1Days", cmd.EscalationT1Days.Value.ToString("G"), ct);
        }

        if (cmd.EscalationT1ElapsedPct.HasValue)
        {
            _thresholds.EscalationT1ElapsedPct = cmd.EscalationT1ElapsedPct.Value;
            await _repo.SetAsync("EscalationT1ElapsedPct", cmd.EscalationT1ElapsedPct.Value.ToString("G"), ct);
        }

        if (cmd.EscalationT3MinHours.HasValue)
        {
            _thresholds.EscalationT3MinHours = cmd.EscalationT3MinHours.Value;
            await _repo.SetAsync("EscalationT3MinHours", cmd.EscalationT3MinHours.Value.ToString("G"), ct);
        }

        if (cmd.EscalationT1MinHours.HasValue)
        {
            _thresholds.EscalationT1MinHours = cmd.EscalationT1MinHours.Value;
            await _repo.SetAsync("EscalationT1MinHours", cmd.EscalationT1MinHours.Value.ToString("G"), ct);
        }

        if (cmd.QaLeadTimeDays.HasValue)
        {
            _thresholds.QaLeadTimeDays = cmd.QaLeadTimeDays.Value;
            await _repo.SetAsync("QaLeadTimeDays", cmd.QaLeadTimeDays.Value.ToString(), ct);
        }

        if (cmd.PointScale is { Count: > 0 })
        {
            _thresholds.PointScale = cmd.PointScale
                .Select(e => new PointScaleEntry { Value = e.Value, Label = e.Label, TimeGuide = e.TimeGuide })
                .ToArray();
            await _repo.SetAsync("PointScale", JsonSerializer.Serialize(_thresholds.PointScale), ct);
        }

        if (cmd.PriorityScale is { Count: > 0 })
        {
            _thresholds.PriorityScale = cmd.PriorityScale
                .Select(e => new PriorityScaleEntry { Value = e.Value, Label = e.Label, Criteria = e.Criteria })
                .ToArray();
            await _repo.SetAsync("PriorityScale", JsonSerializer.Serialize(_thresholds.PriorityScale), ct);
        }

        await _audit.LogAsync("THRESHOLDS_UPDATED", cmd.ActorId, cmd.IpAddress, ct: ct);

        return ServiceResult<ThresholdsDto>.Ok(new ThresholdsDto(
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
            _thresholds.PriorityScale.Select(e => new PriorityScaleEntryDto(e.Value, e.Label, e.Criteria)).ToList()));
    }
}
