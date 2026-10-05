using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Vitals.Queries;

public record ListVitalsQuery(DateOnly? WeekOf, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<VitalsDto>>>;

public class ListVitalsHandler : IRequestHandler<ListVitalsQuery, ServiceResult<IReadOnlyList<VitalsDto>>>
{
    private readonly IVitalsRepository _vitals;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public ListVitalsHandler(IVitalsRepository vitals, IEngineerRepository engineers, ITeamRepository teams)
    {
        _vitals = vitals;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<VitalsDto>>> Handle(ListVitalsQuery query, CancellationToken ct)
    {
        // Individual contributors see only their own vitals history.
        if (query.ActorRole is Roles.Engineer or Roles.Designer)
        {
            var own = await _vitals.ListByEngineerAsync(query.ActorId, ct);
            return ServiceResult<IReadOnlyList<VitalsDto>>.Ok(own.Select(VitalsDto.From).ToList());
        }

        // Non-PMO department heads see only their department's vitals; PMO/PM/Executive/HR see all.
        string? dept = null;
        if (query.ActorRole is not Roles.HeadOfPmo and not Roles.ProjectManager
            and not Roles.HeadOfProduct and not Roles.Executive and not Roles.HR)
        {
            var caller = await _engineers.GetByIdAsync(query.ActorId, ct);
            if (caller?.TeamId is Guid callerTeamId)
            {
                var callerTeam = await _teams.GetByIdAsync(callerTeamId, ct);
                dept = callerTeam?.Department;
            }
        }

        var responses = dept is not null
            ? await _vitals.ListByDepartmentAsync(dept, query.WeekOf, ct)
            : await _vitals.ListAsync(query.WeekOf, ct);

        var engineerIds = responses.Select(r => r.EngineerId).Distinct().ToList();
        var namesById = (await _engineers.GetByIdsAsync(engineerIds, ct)).ToDictionary(e => e.Id, e => e.Name);

        return ServiceResult<IReadOnlyList<VitalsDto>>.Ok(
            responses.Select(r => VitalsDto.FromWithName(r, namesById.GetValueOrDefault(r.EngineerId))).ToList());
    }
}
