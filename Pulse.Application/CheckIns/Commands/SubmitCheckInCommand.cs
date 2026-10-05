using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using MediatR;

namespace Pulse.Application.CheckIns.Commands;

public record SubmitCheckInCommand(
    Guid EngineerId,
    DateOnly Date,
    string Completed,
    string PlannedNext,
    string? Blockers,
    Guid ActorId,
    string? IpAddress,
    Guid? ProjectId = null) : IRequest<ServiceResult<CheckInDto>>;

public class SubmitCheckInHandler : IRequestHandler<SubmitCheckInCommand, ServiceResult<CheckInDto>>
{
    private readonly ICheckInRepository _checkIns;
    private readonly IAuditLogRepository _audit;

    public SubmitCheckInHandler(ICheckInRepository checkIns, IAuditLogRepository audit)
    {
        _checkIns = checkIns;
        _audit = audit;
    }

    public async Task<ServiceResult<CheckInDto>> Handle(SubmitCheckInCommand cmd, CancellationToken ct)
    {
        var existing = await _checkIns.GetByEngineerAndDateAsync(cmd.EngineerId, cmd.Date, cmd.ProjectId, ct);
        if (existing is not null)
        {
            existing.Update(cmd.Completed, cmd.PlannedNext, cmd.Blockers);
            await _checkIns.SaveChangesAsync(ct);

            await _audit.LogAsync("CHECK_IN_UPDATED", cmd.ActorId, cmd.IpAddress,
                $"Engineer {cmd.EngineerId} updated check-in for {cmd.Date:O}", ct);

            return ServiceResult<CheckInDto>.Ok(CheckInDto.From(existing));
        }

        var checkIn = CheckIn.Submit(cmd.EngineerId, cmd.Date, cmd.Completed, cmd.PlannedNext, cmd.Blockers, cmd.ProjectId);

        await _checkIns.AddAsync(checkIn, ct);
        await _checkIns.SaveChangesAsync(ct);

        await _audit.LogAsync("CHECK_IN_SUBMITTED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.EngineerId} submitted check-in for {cmd.Date:O}", ct);

        return ServiceResult<CheckInDto>.Ok(CheckInDto.From(checkIn));
    }
}
