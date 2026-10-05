using Pulse.Application.Common.Interfaces;

namespace Pulse.Application.Tasks;

/// <summary>
/// Hands out sequential per-project task numbers, one repository round trip per project rather
/// than per task — a single-task creation path (CreateTaskCommand) only ever touches one project,
/// so it's one call either way, but a batch path (CSV import, bulk-create, demo seeding) can
/// create dozens of tasks across a handful of projects in one request; without this cache each of
/// those would re-query "what's the next number" and race against numbers this very batch already
/// handed out in memory but hasn't saved yet.
/// </summary>
public class TaskNumberAllocator
{
    private readonly ITaskRepository _tasks;
    private readonly Dictionary<Guid, int> _next = new();

    public TaskNumberAllocator(ITaskRepository tasks) => _tasks = tasks;

    public async Task<int> NextAsync(Guid projectId, CancellationToken ct)
    {
        if (!_next.TryGetValue(projectId, out var number))
            number = await _tasks.GetNextTaskNumberAsync(projectId, ct);

        _next[projectId] = number + 1;
        return number;
    }
}
