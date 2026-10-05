using Pulse.Application.AuditLog;

namespace Pulse.Application.Common.Interfaces;

public interface IAuditLogRepository
{
    Task LogAsync(string action, Guid actorId, string? ipAddress, string? detail = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntryDto>> ListAsync(
        long? afterId, int limit, Guid? actorId, string? action,
        DateTime? from, DateTime? to, CancellationToken ct = default);
}
