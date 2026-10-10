using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Organizations.Queries;

/// <summary>The caller's own organization — for showing its name in the app.</summary>
public record GetCurrentOrganizationQuery : IRequest<ServiceResult<CurrentOrganizationDto>>;

/// <param name="BrandColor">#RRGGBB, or null for Pulse's own colour.</param>
/// <param name="LogoVersion">Changes whenever the logo does (null: no logo) — fetch GET /organization/logo when set.</param>
public record CurrentOrganizationDto(Guid Id, string Name, string Slug, string? BrandColor, string? LogoVersion)
{
    public static CurrentOrganizationDto From(Pulse.Domain.Organizations.Organization o) =>
        new(o.Id, o.Name, o.Slug, o.BrandColor, o.LogoUpdatedAt?.Ticks.ToString());
}

public class GetCurrentOrganizationHandler : IRequestHandler<GetCurrentOrganizationQuery, ServiceResult<CurrentOrganizationDto>>
{
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationRepository _organizations;

    public GetCurrentOrganizationHandler(ICurrentUserService currentUser, IOrganizationRepository organizations)
    {
        _currentUser = currentUser;
        _organizations = organizations;
    }

    public async Task<ServiceResult<CurrentOrganizationDto>> Handle(GetCurrentOrganizationQuery query, CancellationToken ct)
    {
        var organization = _currentUser.OrganizationId is Guid id ? await _organizations.GetByIdAsync(id, ct) : null;
        return organization is null
            ? ServiceResult<CurrentOrganizationDto>.Fail("NOT_FOUND", "Organization not found.")
            : ServiceResult<CurrentOrganizationDto>.Ok(CurrentOrganizationDto.From(organization));
    }
}
