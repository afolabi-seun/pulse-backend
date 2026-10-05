using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AlertConversationRepository : IAlertConversationRepository
{
    private readonly PulseDbContext _db;

    public AlertConversationRepository(PulseDbContext db) => _db = db;

    public async Task<AlertConversation?> FindAsync(string channelId, string threadTs, CancellationToken ct = default) =>
        await _db.AlertConversations.FirstOrDefaultAsync(c => c.ChannelId == channelId && c.ThreadTs == threadTs, ct);

    public async Task AddAsync(AlertConversation conversation, CancellationToken ct = default) =>
        await _db.AlertConversations.AddAsync(conversation, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
