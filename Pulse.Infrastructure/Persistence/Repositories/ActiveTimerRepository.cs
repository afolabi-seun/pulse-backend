using Pulse.Application.Common.Interfaces;
using Pulse.Domain.TimeEntries;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ActiveTimerRepository : IActiveTimerRepository
{
    private readonly PulseDbContext _db;

    public ActiveTimerRepository(PulseDbContext db) => _db = db;

    public async Task<ActiveTimer?> GetByEngineerAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.ActiveTimers.FirstOrDefaultAsync(t => t.EngineerId == engineerId, ct);

    public async Task AddAsync(ActiveTimer timer, CancellationToken ct = default) =>
        await _db.ActiveTimers.AddAsync(timer, ct);

    public Task DeleteAsync(ActiveTimer timer, CancellationToken ct = default)
    {
        _db.ActiveTimers.Remove(timer);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
