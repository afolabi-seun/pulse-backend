using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Vitals.Queries;

/// <summary>Returns the calling engineer's own vitals submissions, newest week first.</summary>
public record GetMyVitalsHistoryQuery(Guid EngineerId) : IRequest<ServiceResult<IReadOnlyList<VitalsDto>>>;

public class GetMyVitalsHistoryHandler : IRequestHandler<GetMyVitalsHistoryQuery, ServiceResult<IReadOnlyList<VitalsDto>>>
{
    private readonly IVitalsRepository _vitals;

    public GetMyVitalsHistoryHandler(IVitalsRepository vitals) => _vitals = vitals;

    public async Task<ServiceResult<IReadOnlyList<VitalsDto>>> Handle(GetMyVitalsHistoryQuery query, CancellationToken ct)
    {
        var responses = await _vitals.ListByEngineerAsync(query.EngineerId, ct);
        return ServiceResult<IReadOnlyList<VitalsDto>>.Ok(responses.Select(VitalsDto.From).ToList());
    }
}
