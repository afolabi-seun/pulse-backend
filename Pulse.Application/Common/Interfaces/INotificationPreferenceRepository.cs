using Pulse.Domain.Notifications;

namespace Pulse.Application.Common.Interfaces;

public interface INotificationPreferenceRepository
{
    Task<IReadOnlyList<NotificationPreference>> ListForEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task<NotificationPreference?> GetAsync(Guid engineerId, string kind, CancellationToken ct = default);
    Task AddAsync(NotificationPreference preference, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
