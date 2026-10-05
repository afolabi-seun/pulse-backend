using Pulse.Domain.TimeEntries;

namespace Pulse.Application.Common.Interfaces;

public interface ITimeEntryRepository
{
    Task<TimeEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TimeEntry>> GetByEngineerAsync(Guid engineerId, int limit, string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<TimeEntry>> GetByEngineerAndDateRangeAsync(Guid engineerId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, decimal>> GetHoursByEngineerInRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default);
    // Same as GetHoursByEngineerInRangeAsync but Task-category entries only — for a utilization
    // figure meant to gauge delivery capacity (Team Utilization's "Hours" column), where blending
    // in Meeting/Admin/Leave/Other would let a week of approved leave read as a normal week of
    // logged work. GetHoursByEngineerInRangeAsync itself stays all-category on purpose — the
    // reminder job and an engineer's own weekly summary both need Leave/Admin counted so someone
    // on leave doesn't get nagged for "not logging time" or look like they under-logged.
    Task<IReadOnlyDictionary<Guid, decimal>> GetDeliveryHoursByEngineerInRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default);
    // Same totals as GetHoursByEngineerInRangeAsync, but broken out per calendar day within the
    // range instead of summed across it — keyed by (EngineerId, Date).
    Task<IReadOnlyDictionary<(Guid EngineerId, DateOnly Date), decimal>> GetDailyHoursByEngineerInRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default);
    // Key is the entry's *effective* project — for a Task-category entry that's the task's own
    // ProjectId (its own ProjectId is always null), for everything else it's the entry's ProjectId
    // directly. Guid.Empty = genuinely unassigned (a category entry with no project picked) — a
    // sentinel, not literal null, because Dictionary<Guid?, TValue> throws on a real null key.
    Task<IReadOnlyDictionary<Guid, decimal>> GetHoursByProjectInRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<Guid>? engineerIds, CancellationToken ct = default);
    /// <summary>The entries behind one line of <see cref="GetHoursByProjectInRangeAsync"/>, each with its task (null for
    /// non-task time, or a task the caller can't see). <paramref name="projectIds"/> selects entries whose effective
    /// project is in the list; null selects entries with no project at all (the "General" line). Same effective-project
    /// rule as the totals, so the two always agree.</summary>
    Task<IReadOnlyList<(Domain.TimeEntries.TimeEntry Entry, Domain.Tasks.PulseTask? Task)>> GetEntriesByProjectInRangeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Guid> engineerIds, IReadOnlyList<Guid>? projectIds, CancellationToken ct = default);
    Task<decimal> GetTotalHoursByTaskAsync(Guid taskId, CancellationToken ct = default);
    Task AddAsync(TimeEntry entry, CancellationToken ct = default);
    Task DeleteAsync(TimeEntry entry, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
