using Pulse.Domain.CheckIns;

namespace Pulse.Application.Common.Interfaces;

public interface ICheckInRepository
{
    Task<CheckIn?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CheckIn?> GetByEngineerAndDateAsync(Guid engineerId, DateOnly date, Guid? projectId, CancellationToken ct = default);
    Task<IReadOnlyList<CheckIn>> GetByEngineerAsync(Guid engineerId, int limit, string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<CheckIn>> GetByDateAsync(DateOnly date, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetEngineersWithoutCheckInTodayAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetEngineersWithoutCheckInOnDateAsync(DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, int>> GetCheckInCountByDateRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, HashSet<Guid>>> GetCheckedInEngineersByProjectAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task AddAsync(CheckIn checkIn, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
