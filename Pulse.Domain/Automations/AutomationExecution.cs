using Pulse.Domain.Common;

namespace Pulse.Domain.Automations;

/// <summary>Records that an AutomationRule has already acted on a task, so AutomationRuleScanner
/// never reassigns the same task twice for the same continuous blocked stretch. Uses the base
/// Entity's CreatedAt as the execution timestamp rather than a duplicate field — the scanner
/// compares it against the task's own ActivatedAt to decide whether this execution is still "fresh"
/// (see AutomationRuleScanner for the exact rule).</summary>
public class AutomationExecution : Entity
{
    public Guid AutomationRuleId { get; private set; }
    public Guid TaskId { get; private set; }

    private AutomationExecution() { }

    public static AutomationExecution Create(Guid automationRuleId, Guid taskId) => new()
    {
        AutomationRuleId = automationRuleId,
        TaskId = taskId,
    };
}
