using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Organizations.Queries;

/// <summary>Every organization with its engineer count. Operator-only (OperatorController).</summary>
public record ListOrganizationsQuery : IRequest<ServiceResult<IReadOnlyList<OrganizationDto>>>;

public class ListOrganizationsHandler : IRequestHandler<ListOrganizationsQuery, ServiceResult<IReadOnlyList<OrganizationDto>>>
{
    private readonly IOrganizationRepository _organizations;

    public ListOrganizationsHandler(IOrganizationRepository organizations) => _organizations = organizations;

    public async Task<ServiceResult<IReadOnlyList<OrganizationDto>>> Handle(ListOrganizationsQuery query, CancellationToken ct)
    {
        var rows = await _organizations.ListWithEngineerCountsAsync(ct);
        return ServiceResult<IReadOnlyList<OrganizationDto>>.Ok(
            rows.Select(r => OrganizationDto.From(r.Organization, r.EngineerCount)).ToList());
    }
}
