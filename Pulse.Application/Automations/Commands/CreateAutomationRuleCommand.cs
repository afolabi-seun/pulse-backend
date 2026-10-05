using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.Automations;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Automations.Commands;

public record CreateAutomationRuleCommand(
    string Name,
    Guid TeamId,
    int ThresholdDays,
    Guid ActorId,
    string ActorRole) : IRequest<ServiceResult<AutomationRuleDto>>;

public class CreateAutomationRuleHandler : IRequestHandler<CreateAutomationRuleCommand, ServiceResult<AutomationRuleDto>>
{
    private readonly IAutomationRuleRepository _rules;
    private readonly ITeamRepository _teams;

    public CreateAutomationRuleHandler(IAutomationRuleRepository rules, ITeamRepository teams)
    {
        _rules = rules;
        _teams = teams;
    }

    public async Task<ServiceResult<AutomationRuleDto>> Handle(CreateAutomationRuleCommand cmd, CancellationToken ct)
    {
        var team = await _teams.GetByIdAsync(cmd.TeamId, ct);
        if (team is null)
            return ServiceResult<AutomationRuleDto>.Fail("NOT_FOUND", $"Team '{cmd.TeamId}' not found.");

        // A plain Team Lead may only automate their own team — mirrors CreateAlertRuleCommand's
        // identical restriction; every other role this endpoint admits (PM-or-above) is unrestricted.
        if (cmd.ActorRole == Roles.TeamLead)
        {
            var ledTeam = await DepartmentScope.GetLedTeamAsync(cmd.ActorId, _teams, ct);
            if (ledTeam is null || ledTeam.Id != team.Id)
                return ServiceResult<AutomationRuleDto>.Fail("FORBIDDEN", "You can only create automation rules for your own team.");
        }

        if (team.TeamLeadId is null)
            return ServiceResult<AutomationRuleDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This team has no team lead set — there is nobody to reassign a blocked task to.");

        AutomationRule rule;
        try
        {
            rule = AutomationRule.Create(cmd.ActorId, cmd.Name, cmd.TeamId, cmd.ThresholdDays);
        }
        catch (DomainException ex)
        {
            return ServiceResult<AutomationRuleDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _rules.AddAsync(rule, ct);
        await _rules.SaveChangesAsync(ct);

        return ServiceResult<AutomationRuleDto>.Ok(AutomationRuleDto.From(rule, team.Name));
    }
}
