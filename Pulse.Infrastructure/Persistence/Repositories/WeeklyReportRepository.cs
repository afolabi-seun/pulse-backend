using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Reports;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class WeeklyReportRepository : IWeeklyReportRepository
{
    private readonly PulseDbContext _db;

    public WeeklyReportRepository(PulseDbContext db) => _db = db;

    public async Task<WeeklyReport?> GetByTeamAndWeekAsync(Guid teamId, DateOnly weekOf, CancellationToken ct = default) =>
        await _db.WeeklyReports.FirstOrDefaultAsync(r => r.TeamId == teamId && r.WeekOf == weekOf, ct);

    public async Task AddAsync(WeeklyReport report, CancellationToken ct = default) =>
        await _db.WeeklyReports.AddAsync(report, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
