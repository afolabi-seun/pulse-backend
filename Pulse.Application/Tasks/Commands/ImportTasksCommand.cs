using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record ImportTaskRow(
    string ProjectName,
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    int Points,
    DateOnly? DueDate,
    TaskType Type,
    string? AssigneeEmail,
    string? EpicName,
    int? Priority = null,
    string? ExternalReference = null,
    // The raw due-date text when the CSV had one that could not be read, so the row fails naming it instead of reporting a "missing" date.
    string? UnreadableDueDate = null);

public record ImportTasksCommand(
    IReadOnlyList<ImportTaskRow> Rows,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<ImportResult>>;

public class ImportTasksHandler : IRequestHandler<ImportTasksCommand, ServiceResult<ImportResult>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IEpicRepository _epics;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public ImportTasksHandler(
        ITaskRepository tasks,
        IProjectRepository projects,
        IEngineerRepository engineers,
        IEpicRepository epics,
        IAuditLogRepository audit,
        IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _epics = epics;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<ImportResult>> Handle(ImportTasksCommand cmd, CancellationToken ct)
    {
        if (cmd.Rows.Count == 0)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "No rows to import.");

        if (cmd.Rows.Count > 500)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "Cannot import more than 500 tasks at once.");

        var projectList = await _projects.ListActiveAsync(ct);
        var projectMap = projectList.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var taskNumbers = new TaskNumberAllocator(_tasks);

        // Build epic map: "ProjectName|EpicTitle" → epicId
        var epicMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projectList)
        {
            var projectEpics = await _epics.ListByProjectAsync(project.Id, ct);
            foreach (var epic in projectEpics)
                epicMap[$"{project.Name}|{epic.Title}"] = epic.Id;
        }

        var engineerMap = (await _engineers.ListActiveAsync(ct))
            .ToDictionary(e => e.Email, e => e.Id, StringComparer.OrdinalIgnoreCase);

        // Same title already in the target project (loaded lazily, one query per distinct project
        // actually referenced) means the row is almost certainly a re-upload rather than a new
        // task — reject it as a duplicate instead of silently creating another copy. Every title
        // this batch itself creates is folded into the same per-project set immediately below, so
        // two identical rows within one file are caught exactly the same way as a re-run import.
        var existingTitlesByProject = new Dictionary<Guid, HashSet<string>>();
        async Task<HashSet<string>> TitlesForProjectAsync(Guid projectId)
        {
            if (existingTitlesByProject.TryGetValue(projectId, out var titles))
                return titles;
            titles = (await _tasks.GetByProjectAsync(projectId, ct))
                .Select(t => t.Title.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            existingTitlesByProject[projectId] = titles;
            return titles;
        }

        var failures = new List<ImportRowFailure>();
        var created = 0;
        var memberPairs = new HashSet<(Guid ProjectId, Guid EngineerId)>();

        for (var i = 0; i < cmd.Rows.Count; i++)
        {
            var row = cmd.Rows[i];
            var rowNum = i + 2;

            if (string.IsNullOrWhiteSpace(row.Title))
            {
                failures.Add(new ImportRowFailure(rowNum, "Title is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.ProjectName))
            {
                failures.Add(new ImportRowFailure(rowNum, "project_name is required."));
                continue;
            }

            if (!projectMap.TryGetValue(row.ProjectName.Trim(), out var projectId))
            {
                failures.Add(new ImportRowFailure(rowNum, $"Project '{row.ProjectName.Trim()}' not found or archived."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.UnreadableDueDate))
            {
                failures.Add(new ImportRowFailure(rowNum, $"Could not read the due date '{row.UnreadableDueDate.Trim()}'. {ImportDateParser.Hint}"));
                continue;
            }

            // A row missing points isn't scoped as real work yet — rather than reject it (and lose
            // the title/description) or invent a value, land it in Backlog for grooming.
            // PulseTask itself only promotes Backlog -> Active once an assignee and valid points
            // are both in place, so an assignee below doesn't pull it out of Backlog early.
            var points = row.Points >= 1 ? row.Points : 0;

            // Rejected here (not just left to PulseTask.Create's own guard) so one bad value in
            // a spreadsheet gets a clear per-row failure instead of an unhandled DomainException
            // aborting the rest of the batch.
            if (points > 13)
            {
                failures.Add(new ImportRowFailure(rowNum, $"points must be 13 or less (got {points})."));
                continue;
            }

            if (row.Priority is < 1 or > 5)
            {
                failures.Add(new ImportRowFailure(rowNum, $"priority must be between 1 and 5 (got {row.Priority})."));
                continue;
            }

            Guid? assigneeId = null;
            if (!string.IsNullOrWhiteSpace(row.AssigneeEmail))
            {
                if (!engineerMap.TryGetValue(row.AssigneeEmail.Trim(), out var eid))
                {
                    failures.Add(new ImportRowFailure(rowNum, $"Engineer '{row.AssigneeEmail.Trim()}' not found or inactive."));
                    continue;
                }
                assigneeId = eid;
            }

            Guid? epicId = null;
            if (!string.IsNullOrWhiteSpace(row.EpicName))
            {
                var epicKey = $"{row.ProjectName.Trim()}|{row.EpicName.Trim()}";
                if (!epicMap.TryGetValue(epicKey, out var eid))
                {
                    failures.Add(new ImportRowFailure(rowNum, $"Epic '{row.EpicName.Trim()}' not found in project '{row.ProjectName.Trim()}'."));
                    continue;
                }
                epicId = eid;
            }

            var trimmedTitle = row.Title.Trim();
            var projectTitles = await TitlesForProjectAsync(projectId);
            if (!projectTitles.Add(trimmedTitle))
            {
                failures.Add(new ImportRowFailure(rowNum,
                    $"A task titled '{trimmedTitle}' already exists in project '{row.ProjectName.Trim()}' — skipped as a likely duplicate. Rename it if this is intentionally a separate task."));
                continue;
            }

            var input = new TaskCreationInput(
                Title: trimmedTitle,
                Description: DescriptionSanitizer.SanitizePlainText(row.Description?.Trim()),
                AcceptanceCriteria: DescriptionSanitizer.SanitizePlainText(row.AcceptanceCriteria?.Trim()),
                Points: points,
                DueDate: row.DueDate,
                ProjectId: projectId,
                AssigneeId: assigneeId,
                Type: row.Type,
                EpicId: epicId,
                RequiresQa: false,
                Discipline: null,
                ParentTaskId: null,
                Priority: row.Priority,
                ActorId: cmd.ActorId,
                ActorRole: cmd.ActorRole,
                TaskNumber: await taskNumbers.NextAsync(projectId, ct),
                ExternalReference: row.ExternalReference?.Trim());

            PulseTask task;
            try
            {
                var built = await TaskCreationPolicy.ValidateAndBuildAsync(input, _projects, _access, ct);
                if (!built.IsSuccess)
                {
                    failures.Add(new ImportRowFailure(rowNum, built.ErrorMessage!));
                    continue;
                }
                task = built.Data!;
            }
            catch (DomainException ex)
            {
                failures.Add(new ImportRowFailure(rowNum, ex.Message));
                continue;
            }

            if (assigneeId.HasValue)
                memberPairs.Add((projectId, assigneeId.Value));

            await _tasks.AddAsync(task, ct);
            created++;
        }

        if (created > 0)
        {
            await _tasks.SaveChangesAsync(ct);

            // Assignment grants project access: ensure each assignee is a member of the
            // project they were assigned into (idempotent, additive).
            foreach (var (pid, eid) in memberPairs)
                await _projects.AddMemberAsync(pid, eid, ct);

            await _audit.LogAsync("TASK_IMPORTED", cmd.ActorId, cmd.IpAddress,
                $"CSV import: created {created} task(s)", ct);
        }

        return ServiceResult<ImportResult>.Ok(new ImportResult(created, failures));
    }
}
