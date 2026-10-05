using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Automations.Commands;

public record DeleteAutomationRuleCommand(Guid RuleId, Guid ActorId) : IRequest<ServiceResult<bool>>;

public class DeleteAutomationRuleHandler : IRequestHandler<DeleteAutomationRuleCommand, ServiceResult<bool>>
{
    private readonly IAutomationRuleRepository _rules;

    public DeleteAutomationRuleHandler(IAutomationRuleRepository rules) => _rules = rules;

    public async Task<ServiceResult<bool>> Handle(DeleteAutomationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(cmd.RuleId, ct);
        if (rule is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Automation rule '{cmd.RuleId}' not found.");
        if (rule.OwnerEngineerId != cmd.ActorId)
            return ServiceResult<bool>.Fail("FORBIDDEN", "You can only delete your own automation rules.");

        await _rules.DeleteAsync(rule, ct);
        await _rules.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }
}
