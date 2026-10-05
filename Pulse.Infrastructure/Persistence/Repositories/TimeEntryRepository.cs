using System.Text;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.TimeEntries;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class TimeEntryRepository : ITimeEntryRepository
{
    private readonly PulseDbContext _db;

    public TimeEntryRepository(PulseDbContext db) => _db = db;

    public async Task<TimeEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.TimeEntries.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<TimeEntry>> GetByEngineerAsync(
        Guid engineerId, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = _db.TimeEntries.Where(t => t.EngineerId == engineerId);

        if (cursor is not null)
        {
            var (cursorDate, cursorId) = DecodeCursor(cursor);
            query = query.Where(t => t.Date < cursorDate || (t.Date == cursorDate && t.Id < cursorId));
        }

        return await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TimeEntry>> GetByEngineerAndDateRangeAsync(
        Guid engineerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await _db.TimeEntries
            .Where(t => t.EngineerId == engineerId && t.Date >= from && t.Date <= to)
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetHoursByEngineerInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default)
    {
        var query = _db.TimeEntries.Where(t => t.Date >= from && t.Date <= to);
        if (engineerIds is { Count: > 0 })
            query = query.Where(t => engineerIds.Contains(t.EngineerId));

        var totals = await query
            .GroupBy(t => t.EngineerId)
            .Select(g => new { EngineerId = g.Key, Hours = g.Sum(t => t.Hours) })
            .ToListAsync(ct);

        return totals.ToDictionary(x => x.EngineerId, x => x.Hours);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetDeliveryHoursByEngineerInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default)
    {
        var query = _db.TimeEntries.Where(t => t.Date >= from && t.Date <= to && t.Category == TimeEntryCategory.Task);
        if (engineerIds is { Count: > 0 })
            query = query.Where(t => engineerIds.Contains(t.EngineerId));

        var totals = await query
            .GroupBy(t => t.EngineerId)
            .Select(g => new { EngineerId = g.Key, Hours = g.Sum(t => t.Hours) })
            .ToListAsync(ct);

        return totals.ToDictionary(x => x.EngineerId, x => x.Hours);
    }

    public async Task<IReadOnlyDictionary<(Guid EngineerId, DateOnly Date), decimal>> GetDailyHoursByEngineerInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default)
    {
        var query = _db.TimeEntries.Where(t => t.Date >= from && t.Date <= to);
        if (engineerIds is { Count: > 0 })
            query = query.Where(t => engineerIds.Contains(t.EngineerId));

        var totals = await query
            .GroupBy(t => new { t.EngineerId, t.Date })
            .Select(g => new { g.Key.EngineerId, g.Key.Date, Hours = g.Sum(t => t.Hours) })
            .ToListAsync(ct);

        return totals.ToDictionary(x => (x.EngineerId, x.Date), x => x.Hours);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetHoursByProjectInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default)
    {
        var query = _db.TimeEntries.Where(t => t.Date >= from && t.Date <= to);
        if (engineerIds is { Count: > 0 })
            query = query.Where(t => engineerIds.Contains(t.EngineerId));

        // A Task-category entry's own ProjectId is always null (see TimeEntry.Validate) — its
        // effective project comes from a left join to the task it references instead.
        // Guid.Empty stands in for "no project" — Dictionary<Guid?, TValue> throws on an actual
        // null key at runtime, so the sentinel is resolved back to null only at the DTO boundary.
        var rows = await query
            .GroupJoin(_db.Tasks, t => t.TaskId, task => (Guid?)task.Id, (t, tasks) => new { t, tasks })
            .SelectMany(x => x.tasks.DefaultIfEmpty(), (x, task) => new
            {
                ProjectId = (x.t.Category == TimeEntryCategory.Task ? task!.ProjectId : x.t.ProjectId) ?? Guid.Empty,
                x.t.Hours,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Hours));
    }

    public async Task<IReadOnlyList<(TimeEntry Entry, Domain.Tasks.PulseTask? Task)>> GetEntriesByProjectInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid> engineerIds, IReadOnlyList<Guid>? projectIds, CancellationToken ct = default)
    {
        // Same effective-project rule as GetHoursByProjectInRangeAsync (Guid.Empty = no project).
        var rows = _db.TimeEntries
            .Where(t => t.Date >= from && t.Date <= to && engineerIds.Contains(t.EngineerId))
            .GroupJoin(_db.Tasks, t => t.TaskId, task => (Guid?)task.Id, (t, tasks) => new { t, tasks })
            .SelectMany(x => x.tasks.DefaultIfEmpty(), (x, task) => new
            {
                Entry = x.t,
                Task = task,
                ProjectId = (x.t.Category == TimeEntryCategory.Task ? task!.ProjectId : x.t.ProjectId) ?? Guid.Empty,
            });

        rows = projectIds is null
            ? rows.Where(r => r.ProjectId == Guid.Empty)
            : rows.Where(r => projectIds.Contains(r.ProjectId));

        var list = await rows.OrderBy(r => r.Entry.Date).ToListAsync(ct);
        return list.Select(r => (r.Entry, r.Task)).ToList();
    }

    public async Task<decimal> GetTotalHoursByTaskAsync(Guid taskId, CancellationToken ct = default) =>
        await _db.TimeEntries.Where(t => t.TaskId == taskId).SumAsync(t => t.Hours, ct);

    public async Task AddAsync(TimeEntry entry, CancellationToken ct = default) =>
        await _db.TimeEntries.AddAsync(entry, ct);

    public Task DeleteAsync(TimeEntry entry, CancellationToken ct = default)
    {
        _db.TimeEntries.Remove(entry);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    private static (DateOnly Date, Guid Id) DecodeCursor(string cursor)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var idx = raw.LastIndexOf('|');
        return (DateOnly.Parse(raw[..idx]), Guid.Parse(raw[(idx + 1)..]));
    }
}
