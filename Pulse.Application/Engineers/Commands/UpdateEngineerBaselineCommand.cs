using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Engineers.Commands;

public record UpdateEngineerBaselineCommand(
    Guid EngineerId,
    int BaselinePoints,
    int BaselineCycleDays,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<EngineerDto>>;

/// <summary>
/// Updates an engineer's workload baseline. Baseline changes are always audit-logged
/// because they directly affect overwork signal thresholds.
/// </summary>
public class UpdateEngineerBaselineHandler : IRequestHandler<UpdateEngineerBaselineCommand, ServiceResult<EngineerDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;

    public UpdateEngineerBaselineHandler(IEngineerRepository engineers, IAuditLogRepository audit)
    {
        _engineers = engineers;
        _audit = audit;
    }

    public async Task<ServiceResult<EngineerDto>> Handle(UpdateEngineerBaselineCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(cmd.EngineerId, ct);
        if (engineer is null || !engineer.IsActive)
            return ServiceResult<EngineerDto>.Fail("NOT_FOUND", $"Engineer '{cmd.EngineerId}' not found.");

        var previousPoints = engineer.BaselinePoints;
        var previousCycle = engineer.BaselineCycleDays;

        engineer.UpdateBaseline(cmd.BaselinePoints, cmd.BaselineCycleDays);
        await _engineers.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            engineerId    = cmd.EngineerId,
            fromPoints    = previousPoints,
            fromCycleDays = previousCycle,
            toPoints      = cmd.BaselinePoints,
            toCycleDays   = cmd.BaselineCycleDays,
        });
        await _audit.LogAsync("BASELINE_CHANGED", cmd.ActorId, cmd.IpAddress, payload, ct);

        return ServiceResult<EngineerDto>.Ok(EngineerDto.From(engineer));
    }
}
