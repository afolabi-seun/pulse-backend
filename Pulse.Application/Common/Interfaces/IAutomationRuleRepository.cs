using Pulse.Domain.Automations;

namespace Pulse.Application.Common.Interfaces;

public interface IAutomationRuleRepository
{
    Task<AutomationRule?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AutomationRule>> ListByOwnerAsync(Guid ownerEngineerId, CancellationToken ct = default);
    Task<IReadOnlyList<AutomationRule>> ListActiveAsync(CancellationToken ct = default);
    Task AddAsync(AutomationRule rule, CancellationToken ct = default);
    Task DeleteAsync(AutomationRule rule, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
