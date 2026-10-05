using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

public record GetEngineerThroughputQuery(Guid EngineerId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<ThroughputWeekDto>>>;

/// <summary>Returns delivered story points per week over the 6-week rolling window ending today,
/// for tasks assigned to the given engineer — the per-engineer analogue of GetProjectThroughputQuery.
/// Access mirrors GetEngineerQuery: self, PM+, Executive/HR/Accountant (org-wide read), or a team lead viewing their own team.</summary>
public class GetEngineerThroughputHandler : IRequestHandler<GetEngineerThroughputQuery, ServiceResult<IReadOnlyList<ThroughputWeekDto>>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;

    public GetEngineerThroughputHandler(IEngineerRepository engineers, ITaskRepository tasks)
    {
        _engineers = engineers;
        _tasks = tasks;
    }

    public async Task<ServiceResult<IReadOnlyList<ThroughputWeekDto>>> Handle(GetEngineerThroughputQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("NOT_FOUND", "Engineer not found.");

        if (query.EngineerId != query.ActorId
            && !Roles.IsOrgReadOnlyViewer(query.ActorRole)
            && !CapabilityRegistry.ResolveFor(query.ActorRole).Contains(CapabilityRegistry.PmOrAbove))
        {
            if (query.ActorRole != Roles.TeamLead)
                return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("FORBIDDEN", "You do not have access to this engineer.");

            var caller = await _engineers.GetByIdAsync(query.ActorId, ct);
            if (caller?.TeamId is null || caller.TeamId != engineer.TeamId)
                return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("FORBIDDEN", "You do not have access to this engineer.");
        }

        var points = await _tasks.GetWeeklyThroughputByAssigneeAsync(query.EngineerId, ct);
        var result = points.Select(p => new ThroughputWeekDto(p.WeekOf, p.PointsDelivered)).ToList();

        return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Ok(result);
    }
}
