using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.CheckIns.Queries;

public record GetCheckInQuery(Guid CheckInId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<CheckInDto>>;

public class GetCheckInHandler : IRequestHandler<GetCheckInQuery, ServiceResult<CheckInDto>>
{
    private readonly ICheckInRepository _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetCheckInHandler(ICheckInRepository checkIns, IEngineerRepository engineers, ITeamRepository teams)
    {
        _checkIns = checkIns;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<CheckInDto>> Handle(GetCheckInQuery query, CancellationToken ct)
    {
        var checkIn = await _checkIns.GetByIdAsync(query.CheckInId, ct);
        if (checkIn is null)
            return ServiceResult<CheckInDto>.Fail("NOT_FOUND", $"Check-in '{query.CheckInId}' not found.");

        if (!await CheckInVisibility.CanViewAsync(query.ActorId, query.ActorRole, checkIn.EngineerId, _engineers, _teams, ct))
            return ServiceResult<CheckInDto>.Fail("FORBIDDEN", "You do not have access to this check-in.");

        return ServiceResult<CheckInDto>.Ok(CheckInDto.From(checkIn));
    }
}
