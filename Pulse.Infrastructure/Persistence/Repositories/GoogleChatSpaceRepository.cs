using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class GoogleChatSpaceRepository : IGoogleChatSpaceRepository
{
    private readonly PulseDbContext _db;

    public GoogleChatSpaceRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<GoogleChatSpace>> ListAllAsync(CancellationToken ct = default) =>
        await _db.GoogleChatSpaces.OrderBy(s => s.DisplayName).ToListAsync(ct);

    public async Task<GoogleChatSpace?> GetBySpaceIdAsync(string spaceId, CancellationToken ct = default) =>
        await _db.GoogleChatSpaces.FirstOrDefaultAsync(s => s.SpaceId == spaceId, ct);

    public Task<GoogleChatSpace?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.GoogleChatSpaces.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task UpsertAsync(string spaceId, string displayName, CancellationToken ct = default)
    {
        var existing = await GetBySpaceIdAsync(spaceId, ct);
        if (existing is not null)
        {
            existing.Refresh(displayName);
        }
        else
        {
            await _db.GoogleChatSpaces.AddAsync(GoogleChatSpace.Create(spaceId, displayName), ct);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public class GoogleChatLinkCodeRepository : IGoogleChatLinkCodeRepository
{
    private readonly PulseDbContext _db;

    public GoogleChatLinkCodeRepository(PulseDbContext db) => _db = db;

    public Task<GoogleChatLinkCode?> GetByHashAsync(string codeHash, CancellationToken ct = default) =>
        _db.GoogleChatLinkCodes.FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

    public async Task AddAsync(GoogleChatLinkCode linkCode, CancellationToken ct = default) =>
        await _db.GoogleChatLinkCodes.AddAsync(linkCode, ct);

    public void Remove(GoogleChatLinkCode linkCode) => _db.GoogleChatLinkCodes.Remove(linkCode);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
