using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Alerts.Queries;

public record GoogleChatSpaceOptionDto(string SpaceId, string DisplayName);

/// <summary>The Google Chat spaces linked to the caller's organization — the only ones an AlertRule can
/// pick, since posting requires the app to be a member and the space to be the org's own (see
/// GoogleChatSpace, multi-tenancy Phase 2c; the org filter does the scoping). Not owner-scoped: which
/// spaces an organization has linked is org-wide setup, not a per-engineer thing.</summary>
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
