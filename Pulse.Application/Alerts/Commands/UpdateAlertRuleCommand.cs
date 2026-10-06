using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Alerts.Commands;

/// <summary>Metric and scope are fixed at creation (see AlertRule.UpdateDetails) — only the
/// name/threshold/comparator/delivery/active fields are editable.</summary>
public record UpdateAlertRuleCommand(
    Guid RuleId,
    string Name,
    AlertComparator Comparator,
    double Threshold,
    bool DeliverInApp,
    bool DeliverEmail,
    bool? IsActive,
    Guid ActorId,
    bool DeliverWebhook = false,
    string? WebhookUrl = null,
    string? SlackChannel = null,
    string? GoogleChatSpaceId = null) : IRequest<ServiceResult<AlertRuleDto>>;

public class UpdateAlertRuleHandler : IRequestHandler<UpdateAlertRuleCommand, ServiceResult<AlertRuleDto>>
{
    private readonly IAlertRuleRepository _rules;

    private readonly ICurrentUserService _currentUser;
    private readonly ISlackInstallationRepository _slack;
    private readonly IGoogleChatSpaceRepository _googleChatSpaces;

    public UpdateAlertRuleHandler(IAlertRuleRepository rules, ICurrentUserService currentUser, ISlackInstallationRepository slack, IGoogleChatSpaceRepository googleChatSpaces)
    {
        _rules = rules;
        _currentUser = currentUser;
        _slack = slack;
        _googleChatSpaces = googleChatSpaces;
    }

    public async Task<ServiceResult<AlertRuleDto>> Handle(UpdateAlertRuleCommand cmd, CancellationToken ct)
    {
        if (await ChatDeliveryAvailability.CheckAsync(_currentUser, _slack, _googleChatSpaces, cmd.SlackChannel, cmd.GoogleChatSpaceId, ct) is string unavailable)
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", unavailable);

        var rule = await _rules.GetByIdAsync(cmd.RuleId, ct);
        if (rule is null)
            return ServiceResult<AlertRuleDto>.Fail("NOT_FOUND", $"Alert rule '{cmd.RuleId}' not found.");
        if (rule.OwnerEngineerId != cmd.ActorId)
            return ServiceResult<AlertRuleDto>.Fail("FORBIDDEN", "You can only edit your own alert rules.");

        try
        {
            rule.UpdateDetails(cmd.Name, cmd.Comparator, cmd.Threshold, cmd.DeliverInApp, cmd.DeliverEmail,
                cmd.DeliverWebhook, cmd.WebhookUrl, cmd.SlackChannel, cmd.GoogleChatSpaceId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        if (cmd.IsActive.HasValue)
            rule.SetActive(cmd.IsActive.Value);

        await _rules.SaveChangesAsync(ct);
        return ServiceResult<AlertRuleDto>.Ok(AlertRuleDto.From(rule));
    }
}
