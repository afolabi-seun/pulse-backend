using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Escalations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class EscalationEventRepository : IEscalationEventRepository
{
    private readonly PulseDbContext _db;

    public EscalationEventRepository(PulseDbContext db) => _db = db;

    public async Task<bool> AlreadyFiredAsync(Guid taskId, EscalationLevel level, CancellationToken ct = default) =>
        await _db.EscalationEvents.AnyAsync(e => e.TaskId == taskId && e.Level == level, ct);

    public async Task RecordAsync(Guid taskId, EscalationLevel level, CancellationToken ct = default)
    {
        await _db.EscalationEvents.AddAsync(EscalationEvent.Record(taskId, level), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        await _db.EscalationEvents
            .Where(e => e.TaskId == taskId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<DateTime?> GetLastFiredAtAsync(CancellationToken ct = default) =>
        await _db.EscalationEvents.MaxAsync(e => (DateTime?)e.FiredAt, ct);

    public async Task<int> GetEscalatedTaskCountAsync(
        Guid assigneeId, DateTime from, DateTime to, Guid? projectId = null, CancellationToken ct = default)
    {
        var taskIdsQuery = _db.Tasks.Where(t => t.AssigneeId == assigneeId);
        if (projectId.HasValue)
            taskIdsQuery = taskIdsQuery.Where(t => t.ProjectId == projectId.Value);
        var taskIds = await taskIdsQuery.Select(t => t.Id).ToListAsync(ct);

        if (taskIds.Count == 0) return 0;

        return await _db.EscalationEvents
            .Where(e => taskIds.Contains(e.TaskId) && e.FiredAt >= from && e.FiredAt <= to)
            .Select(e => e.TaskId)
            .Distinct()
            .CountAsync(ct);
    }

    public async Task<IReadOnlyList<WeeklyEscalationPoint>> GetWeeklyEscalationCountAsync(CancellationToken ct = default)
    {
        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        var firings = await _db.EscalationEvents
            .Where(e => e.FiredAt >= sixWeeksAgo)
            .Select(e => e.FiredAt)
            .ToListAsync(ct);

        return firings
            .GroupBy(firedAt =>
            {
                var dow = (int)firedAt.DayOfWeek;
                return DateOnly.FromDateTime(firedAt.AddDays(dow == 0 ? -6 : 1 - dow));
            })
            .Select(g => new WeeklyEscalationPoint(g.Key, g.Count()))
            .OrderBy(p => p.WeekOf)
            .ToList();
    }
}
