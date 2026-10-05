using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Active engineers org-wide holding a Team-Lead-or-above role — the valid target set for
/// reassigning a pending PR approval request. Deliberately unscoped by department, the same
/// reasoning GetLoanCandidatesQuery/GetQaCandidatesQuery already apply to their own picker: the
/// whole point of reassigning is picking someone other than the (unavailable) default department
/// head, who may need to be in a different department entirely.</summary>
public record GetPrApprovalCandidatesQuery : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class GetPrApprovalCandidatesHandler : IRequestHandler<GetPrApprovalCandidatesQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly IEngineerRepository _engineers;

    public GetPrApprovalCandidatesHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(GetPrApprovalCandidatesQuery query, CancellationToken ct)
    {
        var active = await _engineers.ListActiveAsync(ct);
        var allowedRoles = CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles;
        var candidates = active.Where(e => allowedRoles.Contains(e.Role)).Select(EngineerDto.From).ToList();
        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(candidates);
    }
}
