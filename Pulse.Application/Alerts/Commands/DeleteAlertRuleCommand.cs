using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Alerts.Commands;

public record DeleteAlertRuleCommand(Guid RuleId, Guid ActorId) : IRequest<ServiceResult<bool>>;

public class DeleteAlertRuleHandler : IRequestHandler<DeleteAlertRuleCommand, ServiceResult<bool>>
{
    private readonly IAlertRuleRepository _rules;

    public DeleteAlertRuleHandler(IAlertRuleRepository rules) => _rules = rules;

    public async Task<ServiceResult<bool>> Handle(DeleteAlertRuleCommand cmd, CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(cmd.RuleId, ct);
        if (rule is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Alert rule '{cmd.RuleId}' not found.");
        if (rule.OwnerEngineerId != cmd.ActorId)
            return ServiceResult<bool>.Fail("FORBIDDEN", "You can only delete your own alert rules.");

        await _rules.DeleteAsync(rule, ct);
        await _rules.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }
}
