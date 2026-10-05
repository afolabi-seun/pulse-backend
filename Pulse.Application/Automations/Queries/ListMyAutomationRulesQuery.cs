using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Automations.Queries;

public record ListMyAutomationRulesQuery(Guid ActorId) : IRequest<ServiceResult<IReadOnlyList<AutomationRuleDto>>>;

public class ListMyAutomationRulesHandler : IRequestHandler<ListMyAutomationRulesQuery, ServiceResult<IReadOnlyList<AutomationRuleDto>>>
{
    private readonly IAutomationRuleRepository _rules;
    private readonly ITeamRepository _teams;

    public ListMyAutomationRulesHandler(IAutomationRuleRepository rules, ITeamRepository teams)
    {
        _rules = rules;
        _teams = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<AutomationRuleDto>>> Handle(ListMyAutomationRulesQuery query, CancellationToken ct)
    {
        var rules = await _rules.ListByOwnerAsync(query.ActorId, ct);

        var dtos = new List<AutomationRuleDto>();
        foreach (var rule in rules)
        {
            var team = await _teams.GetByIdAsync(rule.TeamId, ct);
            dtos.Add(AutomationRuleDto.From(rule, team?.Name));
        }

        return ServiceResult<IReadOnlyList<AutomationRuleDto>>.Ok(dtos);
    }
}
