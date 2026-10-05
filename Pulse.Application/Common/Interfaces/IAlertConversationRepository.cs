using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

public interface IAlertConversationRepository
{
    Task<AlertConversation?> FindAsync(string channelId, string threadTs, CancellationToken ct = default);
    Task AddAsync(AlertConversation conversation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
