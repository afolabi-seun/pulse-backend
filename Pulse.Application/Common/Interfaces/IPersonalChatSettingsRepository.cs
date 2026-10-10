using Pulse.Domain.Notifications;

namespace Pulse.Application.Common.Interfaces;

public interface IPersonalChatSettingsRepository
{
    Task<PersonalChatSettings?> GetAsync(Guid engineerId, CancellationToken ct = default);
    /// <summary>The existing row, or a new tracked one with defaults (channel: none).</summary>
    Task<PersonalChatSettings> GetOrCreateAsync(Guid engineerId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Queues a personal chat message for background delivery — never in the request's path.</summary>
public interface IChatNotificationQueue
{
    void Enqueue(Guid recipientId, string text);
}
