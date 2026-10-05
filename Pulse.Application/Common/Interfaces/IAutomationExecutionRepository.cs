using Pulse.Domain.Automations;

namespace Pulse.Application.Common.Interfaces;

public interface IAutomationExecutionRepository
{
    /// <summary>The most recent execution of this rule against this task, if any — used to decide
    /// whether the task's current blocked stretch has already been acted on.</summary>
    Task<AutomationExecution?> GetLatestAsync(Guid automationRuleId, Guid taskId, CancellationToken ct = default);
    Task AddAsync(AutomationExecution execution, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
