using Pulse.Domain.Vitals;

namespace Pulse.Application.Common.Interfaces;

public interface IVitalsRepository
{
    Task<IReadOnlyList<VitalsResponse>> ListAsync(DateOnly? weekOf, CancellationToken ct = default);
    Task<IReadOnlyList<VitalsResponse>> ListByDepartmentAsync(string department, DateOnly? weekOf, CancellationToken ct = default);
    Task<IReadOnlyList<VitalsResponse>> ListByEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task<VitalsResponse?> GetByEngineerAndWeekAsync(Guid engineerId, DateOnly weekOf, CancellationToken ct = default);
    Task<double?> GetAverageScoreAsync(DateOnly weekOf, CancellationToken ct = default);
    Task<int> CountDistinctSourcesAsync(DateOnly weekOf, CancellationToken ct = default);
    Task<VitalsResponse?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(VitalsResponse response, CancellationToken ct = default);
    Task DeleteAsync(VitalsResponse response, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
