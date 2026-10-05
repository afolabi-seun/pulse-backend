using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly PulseDbContext _db;

    public RefreshTokenRepository(PulseDbContext db) => _db = db;

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default) =>
        await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByEngineerAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.RefreshTokens
            .Where(t => t.EngineerID == engineerId && !t.IsRevoked && t.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct = default) =>
        await _db.RefreshTokens.AddAsync(token, ct);

    public async Task RevokeAllForEngineerAsync(Guid engineerId, CancellationToken ct = default)
    {
        var tokens = await _db.RefreshTokens
            .Where(t => t.EngineerID == engineerId && !t.IsRevoked)
            .ToListAsync(ct);

        foreach (var token in tokens)
            token.RevokeAll();
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
