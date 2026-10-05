using Pulse.Domain.Common;

namespace Pulse.Domain.Automations;

/// <summary>A user-configured automation that takes a real action — unlike AlertRule, which only
/// ever explains. Deliberately narrow for this first slice: one action (reassign a task that's been
/// continuously Blocked past a threshold, to its team's lead), fixed rather than a selectable
/// ActionType — a real enum can be introduced when a second action type is built, rather than
/// speculatively generalizing now. Team-scoped only: "reassign to the team lead" has no sensible
/// meaning at project scope the way AlertRule's metrics can be either.</summary>
public class AutomationRule : Entity
{
    public Guid OwnerEngineerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid TeamId { get; private set; }
    /// <summary>How many days a task must have been continuously Blocked before this automation
    /// reassigns it. "Continuously" matters: AutomationRuleScanner only re-fires for the same task
    /// once it's been unblocked and reblocked since the last time this rule acted on it — see
    /// AutomationExecution.</summary>
    public int ThresholdDays { get; private set; }
    public bool IsActive { get; private set; } = true;

    private AutomationRule() { }

    public static AutomationRule Create(Guid ownerEngineerId, string name, Guid teamId, int thresholdDays)
    {
        ValidateName(name);
        ValidateThreshold(thresholdDays);

        return new()
        {
            OwnerEngineerId = ownerEngineerId,
            Name = name.Trim(),
            TeamId = teamId,
            ThresholdDays = thresholdDays,
        };
    }

    /// <summary>Team is fixed at creation — same reasoning as AlertRule.Metric/ScopeType: changing
    /// what a rule watches is close enough to "delete and recreate" that it isn't worth supporting.</summary>
    public void UpdateDetails(string name, int thresholdDays)
    {
        ValidateName(name);
        ValidateThreshold(thresholdDays);

        Name = name.Trim();
        ThresholdDays = thresholdDays;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Name is required.");
    }

    private static void ValidateThreshold(int thresholdDays)
    {
        if (thresholdDays < 1)
            throw new DomainException("Threshold must be at least 1 day.");
    }
}
