using Pulse.Domain.Organizations;

namespace Pulse.Application.Organizations;

public record OrganizationDto(Guid Id, string Name, string Slug, string? BillingEmail, bool IsActive,
    DateTime CreatedAt, int EngineerCount)
{
    public static OrganizationDto From(Organization o, int engineerCount) =>
        new(o.Id, o.Name, o.Slug, o.BillingEmail, o.IsActive, o.CreatedAt, engineerCount);
}

/// <summary>A newly created organization and the first Head invited into it.</summary>
public record CreatedOrganizationDto(OrganizationDto Organization, Guid AdminId, string AdminEmail);
