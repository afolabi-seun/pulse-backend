using Pulse.Domain.TimeEntries;

namespace Pulse.Application.Common.Interfaces;

public interface IActiveTimerRepository
{
    Task<ActiveTimer?> GetByEngineerAsync(Guid engineerId, CancellationToken ct = default);
    Task AddAsync(ActiveTimer timer, CancellationToken ct = default);
    Task DeleteAsync(ActiveTimer timer, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
