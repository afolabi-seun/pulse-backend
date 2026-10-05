using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class GoogleChatThreadRepository : IGoogleChatThreadRepository
{
    private readonly PulseDbContext _db;

    public GoogleChatThreadRepository(PulseDbContext db) => _db = db;

    public async Task<GoogleChatThread?> FindAsync(string spaceId, string threadName, CancellationToken ct = default) =>
        await _db.GoogleChatThreads.FirstOrDefaultAsync(t => t.SpaceId == spaceId && t.ThreadName == threadName, ct);

    public async Task AddAsync(GoogleChatThread thread, CancellationToken ct = default) =>
        await _db.GoogleChatThreads.AddAsync(thread, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
