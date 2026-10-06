using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class NotificationPreferenceRepository : INotificationPreferenceRepository
{
    private readonly PulseDbContext _db;

    public NotificationPreferenceRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<NotificationPreference>> ListForEngineerAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.NotificationPreferences.Where(p => p.EngineerId == engineerId).ToListAsync(ct);

    public Task<NotificationPreference?> GetAsync(Guid engineerId, string kind, CancellationToken ct = default) =>
        _db.NotificationPreferences.FirstOrDefaultAsync(p => p.EngineerId == engineerId && p.Kind == kind, ct);

    public async Task AddAsync(NotificationPreference preference, CancellationToken ct = default) =>
        await _db.NotificationPreferences.AddAsync(preference, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
