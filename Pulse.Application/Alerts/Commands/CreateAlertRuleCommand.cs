using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Alerts.Commands;

public record CreateAlertRuleCommand(
    string Name,
    AlertMetric Metric,
    AlertScopeType ScopeType,
    Guid ScopeId,
    AlertComparator Comparator,
    double Threshold,
    bool DeliverInApp,
    bool DeliverEmail,
    Guid ActorId,
    string ActorRole,
    bool DeliverWebhook = false,
    string? WebhookUrl = null,
    string? SlackChannel = null,
    string? GoogleChatSpaceId = null) : IRequest<ServiceResult<AlertRuleDto>>;

public class CreateAlertRuleHandler : IRequestHandler<CreateAlertRuleCommand, ServiceResult<AlertRuleDto>>
{
    private readonly IAlertRuleRepository _rules;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;
    private readonly ICurrentUserService _currentUser;
    private readonly ISlackInstallationRepository _slack;

    public CreateAlertRuleHandler(
        IAlertRuleRepository rules, ITeamRepository teams, IProjectRepository projects, IProjectAccessPolicy access,
        ICurrentUserService currentUser, ISlackInstallationRepository slack)
    {
        _rules = rules;
        _teams = teams;
        _projects = projects;
        _access = access;
        _currentUser = currentUser;
        _slack = slack;
    }

    public async Task<ServiceResult<AlertRuleDto>> Handle(CreateAlertRuleCommand cmd, CancellationToken ct)
    {
        if (await ChatDeliveryAvailability.CheckAsync(_currentUser, _slack, cmd.SlackChannel, cmd.GoogleChatSpaceId, ct) is string unavailable)
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", unavailable);

        string? scopeName;

        if (cmd.ScopeType == AlertScopeType.Team)
        {
            var team = await _teams.GetByIdAsync(cmd.ScopeId, ct);
            if (team is null)
                return ServiceResult<AlertRuleDto>.Fail("NOT_FOUND", $"Team '{cmd.ScopeId}' not found.");

            // A plain Team Lead may only watch their own team — mirrors AssignTaskCommand's same
            // restriction; every other role this endpoint admits (PM-or-above) is unrestricted.
            if (cmd.ActorRole == Roles.TeamLead)
            {
                var ledTeam = await DepartmentScope.GetLedTeamAsync(cmd.ActorId, _teams, ct);
                if (ledTeam is null || ledTeam.Id != team.Id)
                    return ServiceResult<AlertRuleDto>.Fail("FORBIDDEN", "You can only create alert rules for your own team.");
            }
            scopeName = team.Name;
        }
        else
        {
            var project = await _projects.GetByIdAsync(cmd.ScopeId, ct);
            if (project is null)
                return ServiceResult<AlertRuleDto>.Fail("NOT_FOUND", $"Project '{cmd.ScopeId}' not found.");
            if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
                return ServiceResult<AlertRuleDto>.Fail("FORBIDDEN", "You do not have access to this project.");
            scopeName = project.Name;
        }

        AlertRule rule;
        try
        {
            rule = AlertRule.Create(
                cmd.ActorId, cmd.Name, cmd.Metric, cmd.ScopeType, cmd.ScopeId,
                cmd.Comparator, cmd.Threshold, cmd.DeliverInApp, cmd.DeliverEmail,
                cmd.DeliverWebhook, cmd.WebhookUrl, cmd.SlackChannel, cmd.GoogleChatSpaceId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _rules.AddAsync(rule, ct);
        await _rules.SaveChangesAsync(ct);

        return ServiceResult<AlertRuleDto>.Ok(AlertRuleDto.From(rule, scopeName));
    }
}
