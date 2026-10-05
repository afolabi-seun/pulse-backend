using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Vitals.Commands;

public record DeleteVitalsCommand(Guid VitalsId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<Unit>>;

public class DeleteVitalsHandler : IRequestHandler<DeleteVitalsCommand, ServiceResult<Unit>>
{
    private readonly IVitalsRepository _vitals;
    private readonly IAuditLogRepository _auditLog;

    public DeleteVitalsHandler(IVitalsRepository vitals, IAuditLogRepository auditLog)
    {
        _vitals = vitals;
        _auditLog = auditLog;
    }

    public async Task<ServiceResult<Unit>> Handle(DeleteVitalsCommand command, CancellationToken ct)
    {
        var entry = await _vitals.GetByIdAsync(command.VitalsId, ct);
        if (entry is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Pulse response not found.");

        if (entry.EngineerId != command.ActorId)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You can only delete your own vitals response.");

        // Hash content (score + comment) before deletion for audit accountability
        var raw = $"score:{entry.Score};comment:{entry.Comment ?? string.Empty}";
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        var payload = JsonSerializer.Serialize(new
        {
            vitalsId = entry.Id,
            weekOf = entry.WeekOf.ToString("O"),
            contentHash,
        });

        await _vitals.DeleteAsync(entry, ct);
        await _vitals.SaveChangesAsync(ct);

        await _auditLog.LogAsync("PULSE_DELETED", command.ActorId, command.IpAddress, payload, ct);

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
