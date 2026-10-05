using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

public interface IGoogleChatThreadRepository
{
    Task<GoogleChatThread?> FindAsync(string spaceId, string threadName, CancellationToken ct = default);
    Task AddAsync(GoogleChatThread thread, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
