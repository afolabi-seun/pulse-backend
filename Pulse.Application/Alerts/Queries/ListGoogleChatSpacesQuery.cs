using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Alerts.Queries;

public record GoogleChatSpaceOptionDto(string SpaceId, string DisplayName);

/// <summary>Every Google Chat space the app has actually been added to — the only ones an
/// AlertRule can pick, since posting requires the app to already be a member (see
/// GoogleChatSpace). Not owner-scoped, same reasoning as ListTeamsChannelsQuery had: which spaces
/// the app knows about is server-wide, shared setup, not a per-engineer thing.</summary>
public record ListGoogleChatSpacesQuery : IRequest<ServiceResult<IReadOnlyList<GoogleChatSpaceOptionDto>>>;

public class ListGoogleChatSpacesHandler : IRequestHandler<ListGoogleChatSpacesQuery, ServiceResult<IReadOnlyList<GoogleChatSpaceOptionDto>>>
{
    private readonly IGoogleChatSpaceRepository _spaces;

    public ListGoogleChatSpacesHandler(IGoogleChatSpaceRepository spaces) => _spaces = spaces;

    public async Task<ServiceResult<IReadOnlyList<GoogleChatSpaceOptionDto>>> Handle(ListGoogleChatSpacesQuery query, CancellationToken ct)
    {
        var spaces = await _spaces.ListAllAsync(ct);
        return ServiceResult<IReadOnlyList<GoogleChatSpaceOptionDto>>.Ok(
            spaces.Select(s => new GoogleChatSpaceOptionDto(s.SpaceId, s.DisplayName)).ToList());
    }
}
