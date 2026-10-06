using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using MediatR;

namespace Pulse.Application.Integrations.GoogleChat;

/// <summary>
/// Per-organization Google Chat (multi-tenancy Phase 2c). Every organization shares the one Pulse Chat app,
/// so what's per-org is which spaces belong to whom: a head issues a one-time code here and types
/// "@Pulse link CODE" in a space Pulse has been added to, which links that space to their organization.
/// </summary>
public record GoogleChatSpaceDto(Guid Id, string SpaceId, string DisplayName);
public record GoogleChatConnectionDto(bool Available, IReadOnlyList<GoogleChatSpaceDto> Spaces);
public record GoogleChatLinkCodeDto(string Code, DateTime ExpiresAt, string Command);

// ── Status ───────────────────────────────────────────────────────────────────

public record GetGoogleChatConnectionQuery : IRequest<ServiceResult<GoogleChatConnectionDto>>;

public class GetGoogleChatConnectionHandler(IGoogleChatSpaceRepository spaces, IAppSettings settings)
    : IRequestHandler<GetGoogleChatConnectionQuery, ServiceResult<GoogleChatConnectionDto>>
{
    public async Task<ServiceResult<GoogleChatConnectionDto>> Handle(GetGoogleChatConnectionQuery query, CancellationToken ct)
    {
        // Org-filtered: only this organization's linked spaces (unlinked ones belong to no org).
        var linked = await spaces.ListAllAsync(ct);
        return ServiceResult<GoogleChatConnectionDto>.Ok(new GoogleChatConnectionDto(
            !string.IsNullOrEmpty(settings.GoogleChatServiceAccountJson),
            linked.Select(s => new GoogleChatSpaceDto(s.Id, s.SpaceId, s.DisplayName)).ToList()));
    }
}

// ── Issue a link code ────────────────────────────────────────────────────────

public record CreateGoogleChatLinkCodeCommand(Guid ActorId) : IRequest<ServiceResult<GoogleChatLinkCodeDto>>;

public class CreateGoogleChatLinkCodeHandler(ICurrentUserService currentUser, IGoogleChatLinkCodeRepository codes)
    : IRequestHandler<CreateGoogleChatLinkCodeCommand, ServiceResult<GoogleChatLinkCodeDto>>
{
    public async Task<ServiceResult<GoogleChatLinkCodeDto>> Handle(CreateGoogleChatLinkCodeCommand cmd, CancellationToken ct)
    {
        if (currentUser.OrganizationId is not Guid orgId)
            return ServiceResult<GoogleChatLinkCodeDto>.Fail("FORBIDDEN", "No organization on this session.");

        var (linkCode, code) = GoogleChatLinkCode.Issue(orgId, cmd.ActorId, DateTime.UtcNow);
        await codes.AddAsync(linkCode, ct);
        await codes.SaveChangesAsync(ct);
        return ServiceResult<GoogleChatLinkCodeDto>.Ok(new GoogleChatLinkCodeDto(code, linkCode.ExpiresAt, $"@Pulse link {code}"));
    }
}

// ── Link (from the Chat space) ───────────────────────────────────────────────

/// <summary>Sent by the Chat events endpoint when someone types "@Pulse link CODE" in a space. Anonymous —
/// no caller organization — so lookups see every org; the code is what says which org. Always succeeds with
/// the message to reply in the space, whether or not the link worked.</summary>
public record LinkGoogleChatSpaceCommand(string SpaceId, string DisplayName, string Code) : IRequest<ServiceResult<string>>;

public class LinkGoogleChatSpaceHandler(IGoogleChatLinkCodeRepository codes, IGoogleChatSpaceRepository spaces,
    IOrganizationRepository organizations, IAuditLogRepository audit) : IRequestHandler<LinkGoogleChatSpaceCommand, ServiceResult<string>>
{
    public async Task<ServiceResult<string>> Handle(LinkGoogleChatSpaceCommand cmd, CancellationToken ct)
    {
        var linkCode = await codes.GetByHashAsync(GoogleChatLinkCode.Hash(cmd.Code), ct);
        if (linkCode is null || linkCode.IsExpired(DateTime.UtcNow))
            return Reply("That link code isn't valid or has expired. Create a new one in Pulse under Admin → Integrations.");

        var space = await spaces.GetBySpaceIdAsync(cmd.SpaceId, ct);
        if (space is null)
        {
            await spaces.UpsertAsync(cmd.SpaceId, cmd.DisplayName, ct);
            await spaces.SaveChangesAsync(ct);
            space = (await spaces.GetBySpaceIdAsync(cmd.SpaceId, ct))!;
        }

        if (space.OrganizationId is Guid current && current != linkCode.OrganizationId)
            return Reply("This space is already linked to another organization. It has to be unlinked there first.");

        var organization = await organizations.GetByIdAsync(linkCode.OrganizationId, ct);
        space.LinkTo(linkCode.OrganizationId);
        codes.Remove(linkCode); // single use
        await codes.SaveChangesAsync(ct);

        await audit.LogAsync("GOOGLE_CHAT_SPACE_LINKED", linkCode.CreatedByEngineerId, null,
            $"Linked Google Chat space {cmd.SpaceId} ({cmd.DisplayName}) to organization {linkCode.OrganizationId}", ct);
        return Reply($"Linked this space to {organization?.Name ?? "your organization"}. Pulse alerts can now be sent here.");
    }

    private static ServiceResult<string> Reply(string message) => ServiceResult<string>.Ok(message);
}

// ── Unlink ───────────────────────────────────────────────────────────────────

public record UnlinkGoogleChatSpaceCommand(Guid Id, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<bool>>;

public class UnlinkGoogleChatSpaceHandler(IGoogleChatSpaceRepository spaces, IAuditLogRepository audit)
    : IRequestHandler<UnlinkGoogleChatSpaceCommand, ServiceResult<bool>>
{
    public async Task<ServiceResult<bool>> Handle(UnlinkGoogleChatSpaceCommand cmd, CancellationToken ct)
    {
        // Org-filtered: another organization's space (or an unlinked one) is simply not found.
        var space = await spaces.GetByIdAsync(cmd.Id, ct);
        if (space?.OrganizationId is not Guid orgId)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Space not found.");

        space.Unlink();
        await spaces.SaveChangesAsync(ct);
        await audit.LogAsync("GOOGLE_CHAT_SPACE_UNLINKED", cmd.ActorId, cmd.IpAddress,
            $"Unlinked Google Chat space {space.SpaceId} from organization {orgId}", ct);
        return ServiceResult<bool>.Ok(true);
    }
}
