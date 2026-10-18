using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using MediatR;

namespace Pulse.Application.Reports.Queries;

public record OrgTrendDto(
    IReadOnlyList<ThroughputWeekDto> DeliveryTrend,
    IReadOnlyList<WeeklyEscalationPoint> EscalationTrend);

public record GetOrgTrendQuery : IRequest<ServiceResult<OrgTrendDto>>;

/// <summary>Org-wide delivery and escalation trends over the last 6 weeks — purpose-built for the
/// Executive dashboard, which needs delivery direction and risk trajectory rather than the
/// operational per-team/per-task detail the PMO report gives department heads and team leads.</summary>
public class GetOrgTrendHandler : IRequestHandler<GetOrgTrendQuery, ServiceResult<OrgTrendDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _escalationEvents;

    public GetOrgTrendHandler(ITaskRepository tasks, IEscalationEventRepository escalationEvents)
    {
        _tasks = tasks;
        _escalationEvents = escalationEvents;
    }

    public async Task<ServiceResult<OrgTrendDto>> Handle(GetOrgTrendQuery query, CancellationToken ct)
    {
        var delivered = (await _tasks.GetWeeklyThroughputOrgWideAsync(ct)).ToDictionary(p => p.WeekOf, p => p.PointsDelivered);
        var earned = (await _tasks.GetWeeklyEarnedOrgWideAsync(ct)).ToDictionary(e => e.WeekOf, e => e.PointsEarned);
        var deliveryTrend = delivered.Keys.Union(earned.Keys).OrderBy(w => w)
            .Select(w => new ThroughputWeekDto(w, delivered.GetValueOrDefault(w), earned.GetValueOrDefault(w))).ToList();
        var escalationTrend = await _escalationEvents.GetWeeklyEscalationCountAsync(ct);

        return ServiceResult<OrgTrendDto>.Ok(new OrgTrendDto(deliveryTrend, escalationTrend));
    }
}
