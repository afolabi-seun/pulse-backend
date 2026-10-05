using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Automations.Commands;

/// <summary>Team is fixed at creation (see AutomationRule.UpdateDetails) — only
/// name/threshold/active are editable.</summary>
public record UpdateAutomationRuleCommand(
    Guid RuleId,
    string Name,
    int ThresholdDays,
    bool? IsActive,
    Guid ActorId) : IRequest<ServiceResult<AutomationRuleDto>>;

public class UpdateAutomationRuleHandler : IRequestHandler<UpdateAutomationRuleCommand, ServiceResult<AutomationRuleDto>>
{
    private readonly IAutomationRuleRepository _rules;

    public UpdateAutomationRuleHandler(IAutomationRuleRepository rules) => _rules = rules;

    public async Task<ServiceResult<AutomationRuleDto>> Handle(UpdateAutomationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(cmd.RuleId, ct);
        if (rule is null)
            return ServiceResult<AutomationRuleDto>.Fail("NOT_FOUND", $"Automation rule '{cmd.RuleId}' not found.");
        if (rule.OwnerEngineerId != cmd.ActorId)
            return ServiceResult<AutomationRuleDto>.Fail("FORBIDDEN", "You can only edit your own automation rules.");

        try
        {
            rule.UpdateDetails(cmd.Name, cmd.ThresholdDays);
        }
        catch (DomainException ex)
        {
            return ServiceResult<AutomationRuleDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        if (cmd.IsActive.HasValue)
            rule.SetActive(cmd.IsActive.Value);

        await _rules.SaveChangesAsync(ct);
        return ServiceResult<AutomationRuleDto>.Ok(AutomationRuleDto.From(rule));
    }
}
