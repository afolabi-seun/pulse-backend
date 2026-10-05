using Pulse.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using FeedbackEntity = Pulse.Domain.Feedback.Feedback;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class FeedbackRepository : IFeedbackRepository
{
    private readonly PulseDbContext _db;

    public FeedbackRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<FeedbackEntity>> ListAsync(DateOnly? weekOf, CancellationToken ct = default)
    {
        var query = _db.Feedback.AsQueryable();
        if (weekOf.HasValue)
            query = query.Where(f => f.WeekOf == weekOf.Value);
        return await query.OrderByDescending(f => f.WeekOf).ThenByDescending(f => f.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FeedbackEntity>> ListByDepartmentAsync(
        string department, DateOnly? weekOf, CancellationToken ct = default)
    {
        var engineerIds = await _db.Engineers
            .Where(e => e.TeamId.HasValue)
            .Join(_db.Teams.Where(t => t.Department == department),
                  e => e.TeamId!.Value, t => t.Id, (e, _) => e.Id)
            .ToListAsync(ct);

        var query = _db.Feedback.Where(f => engineerIds.Contains(f.EngineerId));
        if (weekOf.HasValue)
            query = query.Where(f => f.WeekOf == weekOf.Value);
        return await query.OrderByDescending(f => f.WeekOf).ThenByDescending(f => f.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FeedbackEntity>> GetByWeekAsync(DateOnly weekOf, CancellationToken ct = default) =>
        await _db.Feedback.Where(f => f.WeekOf == weekOf).ToListAsync(ct);

    public async Task<int> CountDistinctSourcesAsync(DateOnly weekOf, CancellationToken ct = default) =>
        await _db.Feedback.Where(f => f.WeekOf == weekOf).Select(f => f.EngineerId).Distinct().CountAsync(ct);

    public async Task<IReadOnlyList<(DateOnly WeekOf, int TotalCount, int DistinctSources)>> GetWeekSummariesAsync(
        CancellationToken ct = default)
    {
        var rows = await _db.Feedback
            .GroupBy(f => f.WeekOf)
            .Select(g => new
            {
                WeekOf = g.Key,
                TotalCount = g.Count(),
                DistinctSources = g.Select(f => f.EngineerId).Distinct().Count()
            })
            .OrderByDescending(s => s.WeekOf)
            .ToListAsync(ct);

        return rows.Select(r => (r.WeekOf, r.TotalCount, r.DistinctSources)).ToList();
    }

    public async Task<IReadOnlyList<(DateOnly WeekOf, int TotalCount, int DistinctSources)>> GetWeekSummariesByDepartmentAsync(
        string department, CancellationToken ct = default)
    {
        var engineerIds = await _db.Engineers
            .Where(e => e.TeamId.HasValue)
            .Join(_db.Teams.Where(t => t.Department == department),
                  e => e.TeamId!.Value, t => t.Id, (e, _) => e.Id)
            .ToListAsync(ct);

        var rows = await _db.Feedback
            .Where(f => engineerIds.Contains(f.EngineerId))
            .GroupBy(f => f.WeekOf)
            .Select(g => new
            {
                WeekOf = g.Key,
                TotalCount = g.Count(),
                DistinctSources = g.Select(f => f.EngineerId).Distinct().Count()
            })
            .OrderByDescending(s => s.WeekOf)
            .ToListAsync(ct);

        return rows.Select(r => (r.WeekOf, r.TotalCount, r.DistinctSources)).ToList();
    }

    public async Task<FeedbackEntity?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Feedback.FindAsync([id], ct);

    public async Task AddAsync(FeedbackEntity feedback, CancellationToken ct = default) =>
        await _db.Feedback.AddAsync(feedback, ct);

    public Task DeleteAsync(FeedbackEntity feedback, CancellationToken ct = default)
    {
        _db.Feedback.Remove(feedback);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
