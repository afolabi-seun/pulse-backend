using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class DepartmentThresholdRepository : IDepartmentThresholdRepository
{
    private readonly PulseDbContext _db;

    public DepartmentThresholdRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<DepartmentThresholdOverride>> GetAllAsync(CancellationToken ct = default) =>
        await _db.DepartmentThresholdOverrides.OrderBy(d => d.Department).ToListAsync(ct);

    // Case-insensitive: the admin UI sources department names from existing teams, but a lookup
    // still shouldn't silently miss over a casing difference between where a team's department was
    // typed and where the override was created.
    public async Task<DepartmentThresholdOverride?> GetByDepartmentAsync(string department, CancellationToken ct = default) =>
        await _db.DepartmentThresholdOverrides
            .FirstOrDefaultAsync(d => d.Department.ToLower() == department.ToLower(), ct);

    public async Task UpsertAsync(DepartmentThresholdOverride entity, CancellationToken ct = default)
    {
        var existing = await GetByDepartmentAsync(entity.Department, ct);
        if (existing is null)
        {
            await _db.DepartmentThresholdOverrides.AddAsync(entity, ct);
        }
        else
        {
            existing.LoadVsBaselineRatio = entity.LoadVsBaselineRatio;
            existing.MaxConcurrentTasks = entity.MaxConcurrentTasks;
            existing.StaleCycleMultiplier = entity.StaleCycleMultiplier;
            existing.SignalsRequiredToFlag = entity.SignalsRequiredToFlag;
            existing.UpdatedBy = entity.UpdatedBy;
            existing.UpdatedAt = entity.UpdatedAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string department, CancellationToken ct = default)
    {
        var existing = await GetByDepartmentAsync(department, ct);
        if (existing is null)
            return;

        _db.DepartmentThresholdOverrides.Remove(existing);
        await _db.SaveChangesAsync(ct);
    }
}
