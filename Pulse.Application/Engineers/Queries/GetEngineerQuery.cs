using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

public record GetEngineerQuery(Guid EngineerId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<EngineerDto>>;

public class GetEngineerHandler : IRequestHandler<GetEngineerQuery, ServiceResult<EngineerDto>>
{
    private readonly IEngineerRepository _engineers;

    public GetEngineerHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<EngineerDto>> Handle(GetEngineerQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.EngineerId, ct);
        if (engineer is null || !engineer.IsActive)
            return ServiceResult<EngineerDto>.Fail("NOT_FOUND", $"Engineer '{query.EngineerId}' not found.");

        if (query.EngineerId != query.ActorId
            && query.ActorRole is not (Roles.Executive or Roles.HR or Roles.Accountant)
            && !CapabilityRegistry.ResolveFor(query.ActorRole).Contains(CapabilityRegistry.PmOrAbove))
        {
            // Not self, not PM+, not Executive/HR/Accountant (all org-wide read, same as ListEngineersHandler)
            // — the one other legitimate case is a team lead viewing their own report, matching
            // ListEngineersHandler's "team lead — own team only" scoping.
            if (query.ActorRole != Roles.TeamLead)
                return ServiceResult<EngineerDto>.Fail("FORBIDDEN", "You do not have access to this engineer.");

            var caller = await _engineers.GetByIdAsync(query.ActorId, ct);
            if (caller?.TeamId is null || caller.TeamId != engineer.TeamId)
                return ServiceResult<EngineerDto>.Fail("FORBIDDEN", "You do not have access to this engineer.");
        }

        return ServiceResult<EngineerDto>.Ok(EngineerDto.From(engineer));
    }
}
