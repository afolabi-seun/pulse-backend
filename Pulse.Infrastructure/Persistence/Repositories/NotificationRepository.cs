using System.Text;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly PulseDbContext _db;

    public NotificationRepository(PulseDbContext db) => _db = db;

    public async Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<IReadOnlyList<Notification>> ListForUserAsync(
        Guid userId, bool unreadOnly, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = _db.Notifications.Where(n => n.UserId == userId);

        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);

        if (cursor is not null)
        {
            var (cursorTs, cursorId) = DecodeCursor(cursor);
            query = query.Where(n => n.CreatedAt < cursorTs || (n.CreatedAt == cursorTs && n.Id < cursorId));
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Notification>> ListByKindAsync(string kind, CancellationToken ct = default) =>
        await _db.Notifications.Where(n => n.Kind == kind).ToListAsync(ct);

    public async Task AddAsync(Notification notification, CancellationToken ct = default) =>
        await _db.Notifications.AddAsync(notification, ct);

    public Task DeleteAsync(Notification notification, CancellationToken ct = default)
    {
        _db.Notifications.Remove(notification);
        return Task.CompletedTask;
    }

    public async Task MarkAllReadForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    private static string EncodeCursor(DateTime ts, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ts:O}|{id}"));

    private static (DateTime Ts, Guid Id) DecodeCursor(string cursor)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var idx = raw.LastIndexOf('|');
        return (DateTime.Parse(raw[..idx], null, System.Globalization.DateTimeStyles.RoundtripKind), Guid.Parse(raw[(idx + 1)..]));
    }
}
