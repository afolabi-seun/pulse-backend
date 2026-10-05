using Pulse.Domain.Overrides;

namespace Pulse.Application.Common.Interfaces;

public interface IOverworkOverrideRepository
{
    Task<OverworkOverride?> GetActiveAsync(Guid engineerId, CancellationToken ct = default);
    Task<IReadOnlyList<OverworkOverride>> GetAllActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OverworkOverride>> ListByEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task AddAsync(OverworkOverride @override, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
