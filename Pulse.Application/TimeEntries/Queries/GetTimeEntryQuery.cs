using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record GetTimeEntryQuery(Guid TimeEntryId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<TimeEntryDto>>;

public class GetTimeEntryHandler : IRequestHandler<GetTimeEntryQuery, ServiceResult<TimeEntryDto>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetTimeEntryHandler(ITimeEntryRepository timeEntries, IEngineerRepository engineers, ITeamRepository teams)
    {
        _timeEntries = timeEntries;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<TimeEntryDto>> Handle(GetTimeEntryQuery query, CancellationToken ct)
    {
        var entry = await _timeEntries.GetByIdAsync(query.TimeEntryId, ct);
        if (entry is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Time entry '{query.TimeEntryId}' not found.");

        if (!await TimeEntryVisibility.CanViewAsync(query.ActorId, query.ActorRole, entry.EngineerId, _engineers, _teams, ct))
            return ServiceResult<TimeEntryDto>.Fail("FORBIDDEN", "You do not have access to this time entry.");

        return ServiceResult<TimeEntryDto>.Ok(TimeEntryDto.From(entry));
    }
}
