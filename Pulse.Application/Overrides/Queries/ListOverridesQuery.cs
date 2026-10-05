using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Overrides.Queries;

public record ListOverridesQuery(Guid EngineerId) : IRequest<ServiceResult<IReadOnlyList<OverrideDto>>>;

public class ListOverridesHandler : IRequestHandler<ListOverridesQuery, ServiceResult<IReadOnlyList<OverrideDto>>>
{
    private readonly IOverworkOverrideRepository _overrides;

    public ListOverridesHandler(IOverworkOverrideRepository overrides) => _overrides = overrides;

    public async Task<ServiceResult<IReadOnlyList<OverrideDto>>> Handle(ListOverridesQuery query, CancellationToken ct)
    {
        var overrides = await _overrides.ListByEngineerAsync(query.EngineerId, ct);
        return ServiceResult<IReadOnlyList<OverrideDto>>.Ok(overrides.Select(OverrideDto.From).ToList());
    }
}
