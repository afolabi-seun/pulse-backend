using Pulse.Application.Overwork;

namespace Pulse.Application.Common.Interfaces;

public interface IDepartmentThresholdRepository
{
    Task<IReadOnlyList<DepartmentThresholdOverride>> GetAllAsync(CancellationToken ct = default);
    Task<DepartmentThresholdOverride?> GetByDepartmentAsync(string department, CancellationToken ct = default);
    Task UpsertAsync(DepartmentThresholdOverride entity, CancellationToken ct = default);
    Task DeleteAsync(string department, CancellationToken ct = default);
}
