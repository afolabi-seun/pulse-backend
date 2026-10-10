using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Organizations.Queries;
using Pulse.Domain.Organizations;
using MediatR;

namespace Pulse.Application.Organizations.Commands;

/// <summary>An organization's own look: its display name, accent colour and logo. Changed by the org's head;
/// everyone in the organization sees it.</summary>
public record UpdateOrganizationBrandingCommand(string Name, string? BrandColor, Guid ActorId, string? IpAddress)
    : IRequest<ServiceResult<CurrentOrganizationDto>>;

public class UpdateOrganizationBrandingHandler(ICurrentUserService currentUser, IOrganizationRepository organizations,
    IAuditLogRepository audit) : IRequestHandler<UpdateOrganizationBrandingCommand, ServiceResult<CurrentOrganizationDto>>
{
    public async Task<ServiceResult<CurrentOrganizationDto>> Handle(UpdateOrganizationBrandingCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 200)
            return ServiceResult<CurrentOrganizationDto>.Fail("VALIDATION_ERROR", "Organization name is required (200 characters at most).");
        var color = string.IsNullOrWhiteSpace(cmd.BrandColor) ? null : cmd.BrandColor.Trim();
        if (color is not null && !Organization.IsValidBrandColor(color))
            return ServiceResult<CurrentOrganizationDto>.Fail("VALIDATION_ERROR", "Brand colour must be a hex colour like #B8893B.");

        var organization = currentUser.OrganizationId is Guid id ? await organizations.GetByIdAsync(id, ct) : null;
        if (organization is null)
            return ServiceResult<CurrentOrganizationDto>.Fail("NOT_FOUND", "Organization not found.");

        organization.Rename(cmd.Name);
        organization.SetBrandColor(color);
        await organizations.SaveChangesAsync(ct);
        await audit.LogAsync("ORGANIZATION_BRANDING_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Organization {organization.Id} branding set: name '{organization.Name}', colour {organization.BrandColor ?? "default"}", ct);
        return ServiceResult<CurrentOrganizationDto>.Ok(CurrentOrganizationDto.From(organization));
    }
}

public record SetOrganizationLogoCommand(string ContentType, byte[] Data, Guid ActorId, string? IpAddress)
    : IRequest<ServiceResult<CurrentOrganizationDto>>;

public class SetOrganizationLogoHandler(ICurrentUserService currentUser, IOrganizationRepository organizations,
    IAuditLogRepository audit) : IRequestHandler<SetOrganizationLogoCommand, ServiceResult<CurrentOrganizationDto>>
{
    public async Task<ServiceResult<CurrentOrganizationDto>> Handle(SetOrganizationLogoCommand cmd, CancellationToken ct)
    {
        if (cmd.Data.Length == 0 || cmd.Data.Length > OrganizationLogo.MaxBytes)
            return Invalid($"A logo can be at most {OrganizationLogo.MaxBytes / 1024} KB.");
        // The type comes from the file's own bytes, not from what the upload claimed.
        if (OrganizationLogo.DetectContentType(cmd.Data) is not { } contentType)
            return Invalid("A logo must be a PNG, JPEG or WebP image.");

        var organization = currentUser.OrganizationId is Guid id ? await organizations.GetByIdAsync(id, ct) : null;
        if (organization is null)
            return ServiceResult<CurrentOrganizationDto>.Fail("NOT_FOUND", "Organization not found.");

        var logo = await organizations.GetLogoAsync(organization.Id, ct);
        if (logo is null)
            await organizations.AddLogoAsync(OrganizationLogo.Create(organization.Id, contentType, cmd.Data), ct);
        else
            logo.Replace(contentType, cmd.Data);
        organization.MarkLogoChanged(hasLogo: true);
        await organizations.SaveChangesAsync(ct);
        await audit.LogAsync("ORGANIZATION_LOGO_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Organization {organization.Id} logo set ({contentType}, {cmd.Data.Length} bytes)", ct);
        return ServiceResult<CurrentOrganizationDto>.Ok(CurrentOrganizationDto.From(organization));
    }

    private static ServiceResult<CurrentOrganizationDto> Invalid(string message) =>
        ServiceResult<CurrentOrganizationDto>.Fail("VALIDATION_ERROR", message);
}

public record RemoveOrganizationLogoCommand(Guid ActorId, string? IpAddress) : IRequest<ServiceResult<CurrentOrganizationDto>>;

public class RemoveOrganizationLogoHandler(ICurrentUserService currentUser, IOrganizationRepository organizations,
    IAuditLogRepository audit) : IRequestHandler<RemoveOrganizationLogoCommand, ServiceResult<CurrentOrganizationDto>>
{
    public async Task<ServiceResult<CurrentOrganizationDto>> Handle(RemoveOrganizationLogoCommand cmd, CancellationToken ct)
    {
        var organization = currentUser.OrganizationId is Guid id ? await organizations.GetByIdAsync(id, ct) : null;
        if (organization is null)
            return ServiceResult<CurrentOrganizationDto>.Fail("NOT_FOUND", "Organization not found.");

        if (await organizations.GetLogoAsync(organization.Id, ct) is { } logo)
            organizations.RemoveLogo(logo);
        organization.MarkLogoChanged(hasLogo: false);
        await organizations.SaveChangesAsync(ct);
        await audit.LogAsync("ORGANIZATION_LOGO_REMOVED", cmd.ActorId, cmd.IpAddress, $"Organization {organization.Id} logo removed", ct);
        return ServiceResult<CurrentOrganizationDto>.Ok(CurrentOrganizationDto.From(organization));
    }
}

public record OrganizationLogoDto(string ContentType, byte[] Data);

public record GetOrganizationLogoQuery : IRequest<ServiceResult<OrganizationLogoDto>>;

public class GetOrganizationLogoHandler(ICurrentUserService currentUser, IOrganizationRepository organizations)
    : IRequestHandler<GetOrganizationLogoQuery, ServiceResult<OrganizationLogoDto>>
{
    public async Task<ServiceResult<OrganizationLogoDto>> Handle(GetOrganizationLogoQuery query, CancellationToken ct)
    {
        var logo = currentUser.OrganizationId is Guid id ? await organizations.GetLogoAsync(id, ct) : null;
        return logo is null
            ? ServiceResult<OrganizationLogoDto>.Fail("NOT_FOUND", "No logo.")
            : ServiceResult<OrganizationLogoDto>.Ok(new OrganizationLogoDto(logo.ContentType, logo.Data));
    }
}
