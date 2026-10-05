using Pulse.Application.AuditLog;
using Pulse.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly PulseDbContext _db;

    public AuditLogRepository(PulseDbContext db) => _db = db;

    public async Task LogAsync(string action, Guid actorId, string? ipAddress, string? detail = null, CancellationToken ct = default)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Action = action,
            ActorId = actorId,
            IpAddress = ipAddress,
            Detail = detail,
            Ts = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntryDto>> ListAsync(
        long? afterId, int limit, Guid? actorId, string? action,
        DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _db.AuditLog.AsQueryable();

        if (afterId.HasValue) query = query.Where(a => a.Id < afterId.Value);
        if (actorId.HasValue) query = query.Where(a => a.ActorId == actorId.Value);
        if (action is not null) query = query.Where(a => a.Action == action);
        if (from.HasValue) query = query.Where(a => a.Ts >= from.Value);
        if (to.HasValue) query = query.Where(a => a.Ts <= to.Value);

        return await query
            .OrderByDescending(a => a.Id)
            .Take(limit)
            .Select(a => new AuditLogEntryDto(a.Id, a.Action, a.ActorId, a.IpAddress, a.Detail, a.Ts))
            .ToListAsync(ct);
    }
}
