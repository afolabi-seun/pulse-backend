using System.Text;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.CheckIns.Queries;

public record ListCheckInsQuery(
    Guid ActorId,
    string ActorRole,
    Guid? EngineerId,
    int Limit,
    string? Cursor) : IRequest<ServiceResult<PagedResultDto<CheckInDto>>>;

public class ListCheckInsHandler : IRequestHandler<ListCheckInsQuery, ServiceResult<PagedResultDto<CheckInDto>>>
{
    private readonly ICheckInRepository  _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;

    public ListCheckInsHandler(ICheckInRepository checkIns, IEngineerRepository engineers, ITeamRepository teams)
    {
        _checkIns  = checkIns;
        _engineers = engineers;
        _teams     = teams;
    }

    public async Task<ServiceResult<PagedResultDto<CheckInDto>>> Handle(ListCheckInsQuery query, CancellationToken ct)
    {
        // Individual contributors can only view their own check-in history
        var targetEngineerId = query.ActorRole is Roles.Engineer or Roles.Designer
            ? query.ActorId
            : query.EngineerId ?? query.ActorId;

        if (!await CheckInVisibility.CanViewAsync(query.ActorId, query.ActorRole, targetEngineerId, _engineers, _teams, ct))
            return ServiceResult<PagedResultDto<CheckInDto>>.Fail("FORBIDDEN", "You do not have access to this engineer's check-ins.");

        var limit = Math.Clamp(query.Limit, 1, 100);
        var raw = await _checkIns.GetByEngineerAsync(targetEngineerId, limit + 1, query.Cursor, ct);

        var hasMore = raw.Count > limit;
        var page = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        string? nextCursor = null;
        if (hasMore)
        {
            var last = page[^1];
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.Date:O}|{last.Id}"));
        }

        return ServiceResult<PagedResultDto<CheckInDto>>.Ok(
            new PagedResultDto<CheckInDto>(page.Select(CheckInDto.From).ToList(), nextCursor, hasMore));
    }
}
