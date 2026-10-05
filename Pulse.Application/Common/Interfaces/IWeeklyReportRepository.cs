using Pulse.Domain.Reports;

namespace Pulse.Application.Common.Interfaces;

public interface IWeeklyReportRepository
{
    Task<WeeklyReport?> GetByTeamAndWeekAsync(Guid teamId, DateOnly weekOf, CancellationToken ct = default);
    Task AddAsync(WeeklyReport report, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
