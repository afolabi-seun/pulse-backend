using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

public interface IAlertRuleRepository
{
    Task<AlertRule?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AlertRule>> ListByOwnerAsync(Guid ownerEngineerId, CancellationToken ct = default);
    /// <summary>Every active rule, regardless of owner — read by AlertRuleScanner, which groups
    /// them by (Metric, ScopeType, ScopeId) itself to avoid recomputing the same metric twice.</summary>
    Task<IReadOnlyList<AlertRule>> ListActiveAsync(CancellationToken ct = default);
    Task AddAsync(AlertRule rule, CancellationToken ct = default);
    Task DeleteAsync(AlertRule rule, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
