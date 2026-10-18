using Pulse.Domain.Notifications;

namespace Pulse.Application.Common.Interfaces;

public interface INotificationRepository
{
    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> ListForUserAsync(Guid userId, bool unreadOnly, int limit, string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> ListByKindAsync(string kind, CancellationToken ct = default);
    /// <summary>When each user last received a notification of this kind (users who never have are absent) — lets a digest cover "since the last one".</summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> GetLastCreatedAtByUserAsync(string kind, CancellationToken ct = default);
    Task AddAsync(Notification notification, CancellationToken ct = default);
    Task DeleteAsync(Notification notification, CancellationToken ct = default);
    Task MarkAllReadForUserAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
