using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Thresholds;

public record GetDepartmentThresholdsQuery : IRequest<ServiceResult<IReadOnlyList<DepartmentThresholdDto>>>;

public class GetDepartmentThresholdsHandler : IRequestHandler<GetDepartmentThresholdsQuery, ServiceResult<IReadOnlyList<DepartmentThresholdDto>>>
{
    private readonly IDepartmentThresholdRepository _repo;
    private readonly IEngineerRepository _engineers;

    public GetDepartmentThresholdsHandler(IDepartmentThresholdRepository repo, IEngineerRepository engineers)
    {
        _repo = repo;
        _engineers = engineers;
    }

    public async Task<ServiceResult<IReadOnlyList<DepartmentThresholdDto>>> Handle(GetDepartmentThresholdsQuery query, CancellationToken ct)
    {
        var overrides = await _repo.GetAllAsync(ct);

        var updaterIds = overrides.Where(o => o.UpdatedBy.HasValue).Select(o => o.UpdatedBy!.Value).Distinct().ToList();
        var updaterNames = updaterIds.Count > 0
            ? (await _engineers.GetByIdsAsync(updaterIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var dtos = overrides.Select(o => new DepartmentThresholdDto(
            o.Department,
            o.LoadVsBaselineRatio,
            o.MaxConcurrentTasks,
            o.StaleCycleMultiplier,
            o.SignalsRequiredToFlag,
            o.UpdatedBy.HasValue && updaterNames.TryGetValue(o.UpdatedBy.Value, out var name) ? name : null,
            o.UpdatedAt)).ToList();

        return ServiceResult<IReadOnlyList<DepartmentThresholdDto>>.Ok(dtos);
    }
}
