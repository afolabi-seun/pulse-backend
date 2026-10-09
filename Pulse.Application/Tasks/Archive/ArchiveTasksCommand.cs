using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;
using TaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.Application.Tasks.Archive;

/// <summary>
/// Archives every task created before <paramref name="BaselineStart"/> (a calendar day in the admin's local time, <paramref name="UtcOffsetMinutes"/>
/// minutes from UTC) so the system's working data starts fresh from that day. With <paramref name="DryRun"/> it only reports what it would do.
///
/// Selection: tasks created before the baseline, in any project (or only <paramref name="ProjectIds"/>), except tasks in a personal project (someone's private
/// to-do list is not pilot leftovers). A task's QA task goes with it (a QA task can be created after the cutoff, when its parent is sent to QA).
/// A task that has been TOUCHED since the baseline began (a history entry, a comment, time logged) is live work, so it is kept unless
/// <paramref name="IncludeTouched"/>; a task and its QA task are kept or archived together.
/// </summary>
public record ArchiveTasksCommand(
    DateOnly BaselineStart,
    IReadOnlyList<Guid>? ProjectIds,
    bool IncludeTouched,
    bool DryRun,
    string? Reason,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    int UtcOffsetMinutes = 0) : IRequest<ServiceResult<ArchiveTasksResult>>;

public class ArchiveTasksHandler : IRequestHandler<ArchiveTasksCommand, ServiceResult<ArchiveTasksResult>>
{
    private const int ListCap = 300;

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;

    public ArchiveTasksHandler(ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers, IAuditLogRepository audit)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _audit = audit;
    }

    public async Task<ServiceResult<ArchiveTasksResult>> Handle(ArchiveTasksCommand cmd, CancellationToken ct)
    {
        if (cmd.UtcOffsetMinutes is < -12 * 60 or > 14 * 60)
            return ServiceResult<ArchiveTasksResult>.Fail("VALIDATION_ERROR", "The UTC offset must be between -12 and +14 hours (in minutes).");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (cmd.BaselineStart > today)
            return ServiceResult<ArchiveTasksResult>.Fail("VALIDATION_ERROR", "The baseline start date cannot be in the future.");
        if (!cmd.DryRun && string.IsNullOrWhiteSpace(cmd.Reason))
            return ServiceResult<ArchiveTasksResult>.Fail("VALIDATION_ERROR", "A reason is required to archive tasks.");

        // Midnight at the start of the baseline day, local time, as a UTC instant: a task created at 00:30 local on the baseline day is NOT before it,
        // however the clock reads in UTC.
        var cutoff = new DateTimeOffset(cmd.BaselineStart.ToDateTime(TimeOnly.MinValue), TimeSpan.FromMinutes(cmd.UtcOffsetMinutes)).UtcDateTime;

        var candidates = await _tasks.FindArchiveCandidatesAsync(cutoff, cutoff, cmd.BaselineStart, cmd.ProjectIds, ct);
        var parentIds = candidates.Where(c => c.ParentTaskId is null).Select(c => c.Id).ToList();
        var qaTasks = parentIds.Count == 0 ? [] : await _tasks.FindQaTasksOfAsync(parentIds, cutoff, cmd.BaselineStart, ct);

        var all = candidates.Concat(qaTasks).GroupBy(c => c.Id).Select(g => g.First()).ToList();

        // A task and its QA task are kept or archived together: touched if either is.
        var touchedRoots = all.Where(c => c.TouchedSince).Select(c => c.ParentTaskId ?? c.Id).ToHashSet();
        bool Kept(ArchiveCandidate c) => !cmd.IncludeTouched && touchedRoots.Contains(c.ParentTaskId ?? c.Id);

        var selected = all.Where(c => !Kept(c)).ToList();
        var kept = all.Where(Kept).ToList();

        var projectIds = all.Select(c => c.ProjectId).Distinct().ToList();
        var names = await _projects.GetNamesByIdsAsync(projectIds, ct);
        var codes = await _projects.GetCodesByIdsAsync(projectIds, ct);
        var assigneeIds = selected.Concat(kept).Where(c => c.AssigneeId.HasValue).Select(c => c.AssigneeId!.Value).Distinct().ToList();
        var engineers = assigneeIds.Count == 0 ? [] : await _engineers.GetByIdsAsync(assigneeIds, ct);
        var assigneeName = engineers.ToDictionary(e => e.Id, e => e.Name);

        ArchiveTaskLine Line(ArchiveCandidate c) => new(
            c.Id,
            codes.TryGetValue(c.ProjectId, out var code) && !string.IsNullOrEmpty(code) ? $"{code}-{c.TaskNumber}" : $"#{c.TaskNumber}",
            c.Title, c.Status.ToString(),
            c.AssigneeId.HasValue && assigneeName.TryGetValue(c.AssigneeId.Value, out var a) ? a : null,
            names.GetValueOrDefault(c.ProjectId, "Unknown project"));

        var summaries = selected.GroupBy(c => c.ProjectId).Select(g => new ArchiveProjectSummary(
            g.Key, names.GetValueOrDefault(g.Key, "Unknown project"), g.Count(),
            g.Count(c => c.Status == TaskStatus.Backlog), g.Count(c => c.Status == TaskStatus.Active), g.Count(c => c.Status == TaskStatus.Blocked),
            g.Count(c => c.Status == TaskStatus.InQa), g.Count(c => c.Status == TaskStatus.Paused), g.Count(c => c.Status == TaskStatus.Done)))
            .OrderBy(s => s.ProjectName).ToList();

        var open = selected.Where(c => c.Status != TaskStatus.Done).OrderBy(c => names.GetValueOrDefault(c.ProjectId)).ThenBy(c => c.TaskNumber).ToList();
        var keptOrdered = kept.OrderBy(c => names.GetValueOrDefault(c.ProjectId)).ThenBy(c => c.TaskNumber).ToList();

        var result = new ArchiveTasksResult(
            cmd.DryRun, cmd.BaselineStart, selected.Count, open.Count, selected.Count - open.Count, kept.Count, summaries,
            open.Take(ListCap).Select(Line).ToList(), open.Count > ListCap,
            keptOrdered.Take(ListCap).Select(Line).ToList(), keptOrdered.Count > ListCap);

        if (cmd.DryRun || selected.Count == 0)
            return ServiceResult<ArchiveTasksResult>.Ok(result);

        var entities = await _tasks.GetByIdsAsync(selected.Select(c => c.Id).ToList(), ct);
        var reason = cmd.Reason!.Trim();
        foreach (var task in entities)
        {
            try { task.Archive(cmd.ActorId, reason); }
            catch (DomainException) { /* already archived by someone else in the meantime: nothing to do */ }
        }
        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASKS_ARCHIVED", cmd.ActorId, cmd.IpAddress,
            $"Archived {selected.Count} tasks created before {cmd.BaselineStart:yyyy-MM-dd} ({open.Count} open, {selected.Count - open.Count} done; " +
            $"{kept.Count} kept because touched since). Reason: {reason}. " + string.Join("; ", summaries.Select(s => $"{s.ProjectName}: {s.Total}")), ct);

        return ServiceResult<ArchiveTasksResult>.Ok(result);
    }
}
