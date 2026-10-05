using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using MediatR;

namespace Pulse.Application.Alerts.Queries;

public record ListMyAlertRulesQuery(Guid ActorId) : IRequest<ServiceResult<IReadOnlyList<AlertRuleDto>>>;

public class ListMyAlertRulesHandler : IRequestHandler<ListMyAlertRulesQuery, ServiceResult<IReadOnlyList<AlertRuleDto>>>
{
    private readonly IAlertRuleRepository _rules;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;

    public ListMyAlertRulesHandler(IAlertRuleRepository rules, ITeamRepository teams, IProjectRepository projects)
    {
        _rules = rules;
        _teams = teams;
        _projects = projects;
    }

    public async Task<ServiceResult<IReadOnlyList<AlertRuleDto>>> Handle(ListMyAlertRulesQuery query, CancellationToken ct)
    {
        var rules = await _rules.ListByOwnerAsync(query.ActorId, ct);

        var dtos = new List<AlertRuleDto>();
        foreach (var rule in rules)
        {
            string? scopeName = rule.ScopeType == AlertScopeType.Team
                ? (await _teams.GetByIdAsync(rule.ScopeId, ct))?.Name
                : (await _projects.GetByIdAsync(rule.ScopeId, ct))?.Name;
            dtos.Add(AlertRuleDto.From(rule, scopeName));
        }

        return ServiceResult<IReadOnlyList<AlertRuleDto>>.Ok(dtos);
    }
}
