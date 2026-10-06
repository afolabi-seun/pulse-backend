using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Users;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using MediatR;

namespace Pulse.Application.Organizations.Commands;

public record CreateOrganizationCommand(
    string Name, string Slug, string AdminName, string AdminEmail, string? BillingEmail, string? IpAddress)
    : IRequest<ServiceResult<CreatedOrganizationDto>>;

/// <summary>
/// Creates an organization and invites its first Head (multi-tenancy Phase 2a). Operator-only — reached
/// through OperatorController, never by an org's own users. The Head gets the same activation-link invite
/// as any created user, and from there invites everyone else in their org through the normal users flow:
/// rows they create land in their org automatically (PulseDbContext stamps the caller's org).
///
/// Runs with no caller organization, so lookups here see every org: the slug and email checks are global,
/// which is what both need to be.
/// </summary>
public class CreateOrganizationHandler : IRequestHandler<CreateOrganizationCommand, ServiceResult<CreatedOrganizationDto>>
{
    private const int DefaultBaselinePoints = 20;
    private const int DefaultBaselineCycleDays = 14;

    private readonly IOrganizationRepository _organizations;
    private readonly IEngineerRepository _engineers;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IAppSettings _settings;
    private readonly IEmailQueue _emailQueue;
    private readonly IAuditLogRepository _audit;

    public CreateOrganizationHandler(IOrganizationRepository organizations, IEngineerRepository engineers,
        IPasswordHasher hasher, IJwtService jwt, IAppSettings settings, IEmailQueue emailQueue, IAuditLogRepository audit)
    {
        _organizations = organizations;
        _engineers = engineers;
        _hasher = hasher;
        _jwt = jwt;
        _settings = settings;
        _emailQueue = emailQueue;
        _audit = audit;
    }

    public async Task<ServiceResult<CreatedOrganizationDto>> Handle(CreateOrganizationCommand cmd, CancellationToken ct)
    {
        var slug = cmd.Slug.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return ServiceResult<CreatedOrganizationDto>.Fail("VALIDATION_ERROR", "Organization name is required.");
        if (!Organization.IsValidSlug(slug))
            return ServiceResult<CreatedOrganizationDto>.Fail("VALIDATION_ERROR",
                "Slug must be 2–50 lowercase letters, digits or hyphens, not starting or ending with a hyphen.");
        if (string.IsNullOrWhiteSpace(cmd.AdminName) || string.IsNullOrWhiteSpace(cmd.AdminEmail))
            return ServiceResult<CreatedOrganizationDto>.Fail("VALIDATION_ERROR", "The first admin's name and email are required.");

        if (await _organizations.SlugExistsAsync(slug, ct))
            return ServiceResult<CreatedOrganizationDto>.Fail("CONFLICT", $"An organization with slug '{slug}' already exists.");
        var adminEmail = cmd.AdminEmail.Trim();
        if (await _engineers.GetByEmailAsync(adminEmail, ct) is not null)
            return ServiceResult<CreatedOrganizationDto>.Fail("CONFLICT", $"An account with email '{adminEmail}' already exists.");

        var organization = Organization.Create(cmd.Name, slug, cmd.BillingEmail);
        await _organizations.AddAsync(organization, ct);

        var admin = Engineer.Create(cmd.AdminName.Trim(), adminEmail, ActivationInvite.UnusablePasswordHash(_hasher),
            Roles.HeadOfRnD, DefaultBaselinePoints, DefaultBaselineCycleDays, organization.Id);
        var rawToken = ActivationInvite.IssueToken(admin, _jwt, _settings);
        await _engineers.AddAsync(admin, ct);

        try
        {
            // One SaveChanges for both rows: an organization is never left without its first Head.
            await _engineers.SaveChangesAsync(ct);
        }
        catch (DuplicateEmailException)
        {
            return ServiceResult<CreatedOrganizationDto>.Fail("CONFLICT", $"An account with email '{adminEmail}' already exists.");
        }

        ActivationInvite.Send(_emailQueue, _settings, admin.Name, admin.Email, rawToken, organization.Name);
        await _audit.LogAsync("ORGANIZATION_CREATED", admin.Id, cmd.IpAddress,
            $"Operator created organization {organization.Id} ({slug}) with first Head {admin.Id} ({admin.Email})", ct);

        return ServiceResult<CreatedOrganizationDto>.Ok(
            new CreatedOrganizationDto(OrganizationDto.From(organization, engineerCount: 1), admin.Id, admin.Email));
    }
}
