using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Active, QA-flagged engineers org-wide — the valid target set for assigning a QA
/// review task. Deliberately unscoped by the caller's department: unlike ListEngineersQuery
/// (a roster/workload view, correctly department-scoped for most heads), a QA reviewer is
/// routinely in a different department from the work being reviewed, so a department head
/// editing a QA task needs to see every QA engineer, not just their own department's — the
/// same reasoning GetLoanCandidatesQuery already applies to loan targets.</summary>
public record GetQaCandidatesQuery : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class GetQaCandidatesHandler : IRequestHandler<GetQaCandidatesQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly IEngineerRepository _engineers;

    public GetQaCandidatesHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(GetQaCandidatesQuery query, CancellationToken ct)
    {
        var active = await _engineers.ListActiveAsync(ct);
        var qaEngineers = active.Where(e => e.IsQa).Select(EngineerDto.From).ToList();
        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(qaEngineers);
    }
}
