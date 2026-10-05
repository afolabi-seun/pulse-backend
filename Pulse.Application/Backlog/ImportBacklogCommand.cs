using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Application.Tasks;
using Pulse.Domain.Epics;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Backlog;

public record ImportBacklogRow(
    string ProjectName,
    string? EpicRef,
    string EpicName,
    string? Feature,
    string? StoryRef,
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    string? Priority,
    string? Type,
    int? StoryPoints,
    string? Phase,
    string? Notes,
    string? OwnerTeam = null);

public record ImportBacklogCommand(
    IReadOnlyList<ImportBacklogRow> Rows,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<ImportResult>>;

public class ImportBacklogHandler : IRequestHandler<ImportBacklogCommand, ServiceResult<ImportResult>>
{
    private readonly ITaskRepository     _tasks;
    private readonly IProjectRepository  _projects;
    private readonly IEpicRepository     _epics;
    private readonly ITeamRepository     _teams;
    private readonly IAuditLogRepository _audit;

    public ImportBacklogHandler(
        ITaskRepository tasks,
        IProjectRepository projects,
        IEpicRepository epics,
        ITeamRepository teams,
        IAuditLogRepository audit)
    {
        _tasks    = tasks;
        _projects = projects;
        _epics    = epics;
        _teams    = teams;
        _audit    = audit;
    }

    public async Task<ServiceResult<ImportResult>> Handle(ImportBacklogCommand cmd, CancellationToken ct)
    {
        if (cmd.Rows.Count == 0)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "No rows to import.");

        if (cmd.Rows.Count > 1000)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "Cannot import more than 1000 backlog items at once.");

        // Build team name → ID map for owner_team column resolution
        var allTeams = await _teams.ListAllAsync(ct);
        var teamByName = allTeams.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);

        // Collect the first non-empty owner_team per project_name from all rows
        var projectOwnerTeam = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cmd.Rows)
        {
            if (string.IsNullOrWhiteSpace(r.ProjectName)) continue;
            var key = r.ProjectName.Trim();
            if (!projectOwnerTeam.ContainsKey(key))
            {
                Guid? teamId = null;
                if (!string.IsNullOrWhiteSpace(r.OwnerTeam) && teamByName.TryGetValue(r.OwnerTeam.Trim(), out var tid))
                    teamId = tid;
                projectOwnerTeam[key] = teamId;
            }
        }

        // Build project cache: name → id (auto-create unknown projects)
        var projectList = await _projects.ListActiveAsync(ct);
        var projectMap = projectList.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var usedCodes = new HashSet<string>(await _projects.GetAllCodesAsync(ct));
        var taskNumbers = new TaskNumberAllocator(_tasks);

        // Build epic cache: "projectId|epicName" → epicId (auto-create unknown epics per project)
        var epicMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projectList)
        {
            var projectEpics = await _epics.ListByProjectAsync(project.Id, ct);
            foreach (var epic in projectEpics)
                epicMap[$"{project.Id}|{epic.Title}"] = epic.Id;
        }

        var failures = new List<ImportRowFailure>();
        var created = 0;

        for (var i = 0; i < cmd.Rows.Count; i++)
        {
            var row = cmd.Rows[i];
            var rowNum = i + 2;

            if (string.IsNullOrWhiteSpace(row.Title))
            {
                failures.Add(new ImportRowFailure(rowNum, "title is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.ProjectName))
            {
                failures.Add(new ImportRowFailure(rowNum, "project_name is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.EpicName))
            {
                failures.Add(new ImportRowFailure(rowNum, "epic_name is required."));
                continue;
            }

            // Auto-create project if it doesn't exist yet
            var pname = row.ProjectName.Trim();
            if (!projectMap.TryGetValue(pname, out var projectId))
            {
                var ownerTeamId = projectOwnerTeam.TryGetValue(pname, out var ot) ? ot : null;
                var code = ProjectCodeGenerator.MakeUnique(ProjectCodeGenerator.DeriveBase(pname), usedCodes);
                var newProject = Project.Create(pname, ownerTeamId: ownerTeamId, code: code);
                await _projects.AddAsync(newProject, ct);
                await _projects.SaveChangesAsync(ct);
                projectId = newProject.Id;
                projectMap[pname] = projectId;
            }

            // Auto-create epic within the project if it doesn't exist yet
            var ename = row.EpicName.Trim();
            var epicKey = $"{projectId}|{ename}";
            if (!epicMap.TryGetValue(epicKey, out var epicId))
            {
                var newEpic = Epic.Create(ename, projectId);
                await _epics.AddAsync(newEpic, ct);
                await _epics.SaveChangesAsync(ct);
                epicId = newEpic.Id;
                epicMap[epicKey] = epicId;
            }

            var taskType = ParseType(row.Type);
            var points = row.StoryPoints ?? 0;
            // Rejected here (not just left to PulseTask.Create's own guard) so one bad value in
            // a spreadsheet gets a clear per-row failure instead of an unhandled DomainException
            // aborting the rest of the batch.
            if (points is < 0 or > 13)
            {
                failures.Add(new ImportRowFailure(rowNum, $"story_points must be between 0 and 13 (got {points})."));
                continue;
            }
            var priority = ParsePriority(row.Priority);

            var description = BuildDescription(row);

            var task = PulseTask.Create(row.Title.Trim(), points, projectId, taskType, createdById: cmd.ActorId);
            task.AssignTaskNumber(await taskNumbers.NextAsync(projectId, ct));
            task.AssignToEpic(epicId);
            if (priority.HasValue)
                task.SetPriority(priority.Value);
            if (!string.IsNullOrWhiteSpace(row.StoryRef))
                task.SetExternalReference(row.StoryRef.Trim());

            if (!string.IsNullOrWhiteSpace(description) || !string.IsNullOrWhiteSpace(row.AcceptanceCriteria))
                task.UpdateDetails(task.Title, DescriptionSanitizer.SanitizePlainText(description),
                    DescriptionSanitizer.SanitizePlainText(row.AcceptanceCriteria?.Trim()), points, null, cmd.ActorId);

            await _tasks.AddAsync(task, ct);
            created++;
        }

        if (created > 0)
        {
            await _tasks.SaveChangesAsync(ct);
            await _audit.LogAsync("BACKLOG_IMPORTED", cmd.ActorId, cmd.IpAddress,
                $"Backlog import: created {created} item(s)", ct);
        }

        return ServiceResult<ImportResult>.Ok(new ImportResult(created, failures));
    }

    private static TaskType ParseType(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "bug"   => TaskType.Bug,
        "chore" => TaskType.Chore,
        _       => TaskType.Feature,
    };

    /// <summary>Maps the backlog CSV's free-text priority column onto the task's real 1-5 Priority
    /// field. Real-world exports use MoSCoW terms (must_have/should_have/could_have/wont_have —
    /// see pulse_backlog_template.csv), so that's the primary format; a plain 1-5 number, "P1".."P5",
    /// or a Low/Medium/High/Critical/Urgent word are also accepted since the column is genuinely
    /// freeform. An unrecognized or blank value leaves the task unprioritized rather than failing
    /// the row — priority is a triage aid, not something worth blocking an otherwise-valid import over.</summary>
    private static int? ParsePriority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var normalized = raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

        return normalized switch
        {
            "must_have" or "must" or "critical" or "urgent" or "p5" or "5" => 5,
            "should_have" or "should" or "high" or "p4" or "4"             => 4,
            "medium" or "med" or "p3" or "3"                               => 3,
            "could_have" or "could" or "low" or "p2" or "2"                => 2,
            "wont_have" or "won't_have" or "wont" or "won't" or "p1" or "1" => 1,
            _ => null,
        };
    }

    private static string? BuildDescription(ImportBacklogRow row)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(row.Feature))
            parts.Add($"Feature: {row.Feature.Trim()}");

        if (!string.IsNullOrWhiteSpace(row.Description))
            parts.Add(row.Description.Trim());

        if (!string.IsNullOrWhiteSpace(row.Phase))
            parts.Add($"Phase: {row.Phase.Trim()}");

        if (!string.IsNullOrWhiteSpace(row.EpicRef))
            parts.Add($"Epic ref: {row.EpicRef.Trim()}");

        if (!string.IsNullOrWhiteSpace(row.Notes))
            parts.Add($"Notes: {row.Notes.Trim()}");

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }
}
