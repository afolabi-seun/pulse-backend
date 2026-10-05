using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ProjectActivityDto(
    Guid Id,
    Guid TaskId,
    string TaskTitle,
    Guid ActorId,
    string ActorName,
    string Summary,
    DateTime ChangedAt);

public record GetProjectActivityQuery(Guid ProjectId, Guid ActorId, string ActorRole, int Limit = 50, string? Cursor = null)
    : IRequest<ServiceResult<PagedResult<ProjectActivityDto>>>;

/// <summary>Returns the most recent task-history events across every task in a project — a per-project
/// activity feed. Distinct from the personal Notifications inbox: this shows what happened on the
/// project regardless of who it's relevant to, rather than what's relevant to the caller.</summary>
public class GetProjectActivityHandler : IRequestHandler<GetProjectActivityQuery, ServiceResult<PagedResult<ProjectActivityDto>>>
{
    private static readonly Dictionary<string, string> StatusLabels = new()
    {
        ["Active"] = "Active", ["Blocked"] = "Blocked", ["InQa"] = "In QA", ["Done"] = "Done", ["Paused"] = "Paused",
    };

    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public GetProjectActivityHandler(IProjectRepository projects, ITaskRepository tasks, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _projects = projects;
        _tasks = tasks;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<PagedResult<ProjectActivityDto>>> Handle(GetProjectActivityQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<PagedResult<ProjectActivityDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<PagedResult<ProjectActivityDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var limit = Math.Clamp(query.Limit, 1, 100);
        var (entries, nextCursor) = await _tasks.GetRecentActivityByProjectAsync(query.ProjectId, limit, query.Cursor, ct);

        var assigneeRefIds = entries
            .Where(e => e.Field == "assignee_id")
            .SelectMany(e => new[] { e.OldValue, e.NewValue })
            .Where(v => v is not null)
            .Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value);

        var engineerIds = entries.Select(e => e.ActorId).Concat(assigneeRefIds).Distinct().ToList();

        var names = engineerIds.Count > 0
            ? (await _engineers.GetByIdsAsync(engineerIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var dtos = entries.Select(e => new ProjectActivityDto(
            e.Id, e.TaskId, e.TaskTitle,
            e.ActorId, names.TryGetValue(e.ActorId, out var actorName) ? actorName : "Someone",
            BuildSummary(e, names), e.ChangedAt)).ToList();

        return ServiceResult<PagedResult<ProjectActivityDto>>.Ok(new PagedResult<ProjectActivityDto>(dtos, nextCursor, nextCursor is not null));
    }

    private static string BuildSummary(ProjectActivityEntry e, IReadOnlyDictionary<Guid, string> names) => e.Field switch
    {
        "status" => $"moved from {Label(e.OldValue)} to {Label(e.NewValue)}",
        "assignee_id" when e.Context == "loaned" => $"loaned to {ResolveName(e.NewValue, names)}",
        "assignee_id" when e.Context == "recalled" => $"recalled back to {ResolveName(e.NewValue, names)}",
        "assignee_id" => e.OldValue is null
            ? $"assigned to {ResolveName(e.NewValue, names)}"
            : e.NewValue is null
                ? "unassigned"
                : $"reassigned from {ResolveName(e.OldValue, names)} to {ResolveName(e.NewValue, names)}",
        "type" => $"type changed from {e.OldValue} to {e.NewValue}",
        "title" => "title updated",
        "points" when !string.IsNullOrWhiteSpace(e.Reason) =>
            $"changed points from {e.OldValue ?? "none"} to {e.NewValue ?? "none"} — reason: {e.Reason}",
        "points" => $"points changed from {e.OldValue ?? "none"} to {e.NewValue ?? "none"}",
        "due_date" when e.OldValue is not null && !string.IsNullOrWhiteSpace(e.Reason) =>
            $"changed due date from {DateLabel(e.OldValue)} to {DateLabel(e.NewValue)} — reason: {e.Reason}",
        "due_date" => "due date updated",
        "reactivation_reason" => $"reactivated: {e.NewValue}",
        "subtask_completed" => $"completed subtask '{e.NewValue}'",
        _ => $"{e.Field} updated",
    };

    private static string DateLabel(string? iso) =>
        iso is not null && DateOnly.TryParse(iso, out var d) ? d.ToString("d MMM yyyy") : "none";

    private static string Label(string? status) => status is not null && StatusLabels.TryGetValue(status, out var label) ? label : status ?? "—";

    private static string ResolveName(string? id, IReadOnlyDictionary<Guid, string> names) =>
        id is not null && Guid.TryParse(id, out var guid) && names.TryGetValue(guid, out var name) ? name : "someone";
}
