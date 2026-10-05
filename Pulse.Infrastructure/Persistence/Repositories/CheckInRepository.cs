using System.Text;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class CheckInRepository : ICheckInRepository
{
    private readonly PulseDbContext _db;

    public CheckInRepository(PulseDbContext db) => _db = db;

    public async Task<CheckIn?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.CheckIns.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<CheckIn?> GetByEngineerAndDateAsync(Guid engineerId, DateOnly date, Guid? projectId, CancellationToken ct = default) =>
        await _db.CheckIns.FirstOrDefaultAsync(c => c.EngineerId == engineerId && c.Date == date && c.ProjectId == projectId, ct);

    public async Task<IReadOnlyList<CheckIn>> GetByEngineerAsync(
        Guid engineerId, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = _db.CheckIns.Where(c => c.EngineerId == engineerId);

        if (cursor is not null)
        {
            var (cursorDate, cursorId) = DecodeCursor(cursor);
            query = query.Where(c => c.Date < cursorDate || (c.Date == cursorDate && c.Id < cursorId));
        }

        return await query
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CheckIn>> GetByDateAsync(DateOnly date, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default)
    {
        var query = _db.CheckIns.Where(c => c.Date == date);
        if (engineerIds is { Count: > 0 })
            query = query.Where(c => engineerIds.Contains(c.EngineerId));
        return await query.OrderBy(c => c.EngineerId).ToListAsync(ct);
    }

    public Task<IReadOnlyList<Guid>> GetEngineersWithoutCheckInTodayAsync(CancellationToken ct = default) =>
        GetEngineersWithoutCheckInOnDateAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);

    public async Task<IReadOnlyList<Guid>> GetEngineersWithoutCheckInOnDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var checkedInIds = await _db.CheckIns
            .Where(c => c.Date == date)
            .Select(c => c.EngineerId)
            .ToListAsync(ct);

        return await _db.Engineers
            .Where(e => e.IsActive
                     && !checkedInIds.Contains(e.Id)
                     && _db.Tasks.Any(t => t.AssigneeId == e.Id && t.Status != Domain.Tasks.TaskStatus.Done))
            .Select(e => e.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetCheckInCountByDateRangeAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // Distinct (engineer, date) pairs, not raw rows — an engineer can now have more than one
        // row for the same day (one per project auto-checked-in), and this counts DAYS checked in,
        // not check-ins submitted.
        var counts = await _db.CheckIns
            .Where(c => c.Date >= from && c.Date <= to)
            .Select(c => new { c.EngineerId, c.Date })
            .Distinct()
            .GroupBy(c => c.EngineerId)
            .Select(g => new { EngineerId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(x => x.EngineerId, x => x.Count);
    }

    public async Task<IReadOnlyDictionary<Guid, HashSet<Guid>>> GetCheckedInEngineersByProjectAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await _db.CheckIns
            .Where(c => c.Date >= from && c.Date <= to && c.ProjectId != null)
            .Select(c => new { ProjectId = c.ProjectId!.Value, c.EngineerId })
            .Distinct()
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.EngineerId).ToHashSet());
    }

    public async Task AddAsync(CheckIn checkIn, CancellationToken ct = default) =>
        await _db.CheckIns.AddAsync(checkIn, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    private static string EncodeCursor(DateOnly date, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{date:O}|{id}"));

    private static (DateOnly Date, Guid Id) DecodeCursor(string cursor)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var idx = raw.LastIndexOf('|');
        return (DateOnly.Parse(raw[..idx]), Guid.Parse(raw[(idx + 1)..]));
    }
}
