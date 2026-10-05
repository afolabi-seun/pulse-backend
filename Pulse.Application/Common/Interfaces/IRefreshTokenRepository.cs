using Pulse.Domain.Engineers;

namespace Pulse.Application.Common.Interfaces;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<RefreshToken>> GetActiveByEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task AddAsync(RefreshToken token, CancellationToken ct = default);
    Task RevokeAllForEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
