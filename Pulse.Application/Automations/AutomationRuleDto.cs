using Pulse.Domain.Automations;

namespace Pulse.Application.Automations;

public record AutomationRuleDto(
    Guid Id,
    string Name,
    Guid TeamId,
    string? TeamName,
    int ThresholdDays,
    bool IsActive,
    DateTime CreatedAt)
{
    public static AutomationRuleDto From(AutomationRule r, string? teamName = null) => new(
        r.Id, r.Name, r.TeamId, teamName, r.ThresholdDays, r.IsActive, r.CreatedAt);
}
