using Pulse.Domain.Notifications;

namespace Pulse.Application.Common.Interfaces;

public interface INotificationRepository
{
    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> ListForUserAsync(Guid userId, bool unreadOnly, int limit, string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> ListByKindAsync(string kind, CancellationToken ct = default);
    Task AddAsync(Notification notification, CancellationToken ct = default);
    Task DeleteAsync(Notification notification, CancellationToken ct = default);
    Task MarkAllReadForUserAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
