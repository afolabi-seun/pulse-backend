using System.Security.Claims;
using System.Text;
using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Backlog;
using Pulse.Application.Common;
using Pulse.Application.Projects.Commands;
using Pulse.Application.Tasks.Commands;
using Pulse.Application.Users.Commands;
using Pulse.Domain.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/import")]
[Tags("Import")]
[RequiresCapability(CapabilityRegistry.PmOrAbove)]
public class ImportController : ControllerBase
{
    private readonly IMediator _mediator;

    public ImportController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns a CSV template for bulk-importing projects.</summary>
    [HttpGet("template/projects")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public IActionResult ProjectsTemplate()
    {
        const string csv = "name,description,owner_team\r\nAlpha Project,An example project description,Engineering\r\nBeta Project,,\r\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "pulse_projects_template.csv");
    }

    /// <summary>Returns a CSV template for bulk-importing tasks.</summary>
    [HttpGet("template/tasks")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public IActionResult TasksTemplate()
    {
        const string csv =
            "project_name,title,description,acceptance_criteria,points,due_date,priority,type,assignee_email,epic_name,external_reference\r\n" +
            "Alpha Project,Implement login,,\"Given valid credentials, when submitted, then the user reaches the dashboard.\",5,2026-07-01,2,Feature,alice@company.com,User Authentication,JIRA-482\r\n" +
            "Alpha Project,Fix bug #123,,,2,2026-07-15,,Bug,,,\r\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "pulse_tasks_template.csv");
    }

    /// <summary>Bulk-imports projects from a CSV file (max 500 rows). Columns: name, description. PMO only.</summary>
    [HttpPost("projects")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ImportProjects(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", "No file uploaded."));

        List<ImportProjectRow> rows;
        try
        {
            rows = ParseProjectsCsv(file.OpenReadStream());
        }
        catch (FormatException ex)
        {
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", ex.Message));
        }

        return (await _mediator.Send(new ImportProjectsCommand(rows, GetActorId(), GetIp()), ct)).ToActionResult();
    }

    /// <summary>
    /// Bulk-imports tasks from a CSV file (max 500 rows).
    /// Columns: project_name, title, description, acceptance_criteria, points, due_date, priority, type, assignee_email.
    /// Projects are matched by name (case-insensitive). Engineers are matched by email.
    /// </summary>
    [HttpPost("tasks")]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ImportTasks(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", "No file uploaded."));

        List<ImportTaskRow> rows;
        try
        {
            rows = ParseTasksCsv(file.OpenReadStream());
        }
        catch (FormatException ex)
        {
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", ex.Message));
        }

        var callerRole = User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty;
        return (await _mediator.Send(new ImportTasksCommand(rows, GetActorId(), GetIp(), callerRole), ct)).ToActionResult();
    }

    /// <summary>Returns the canonical backlog CSV template for use with /import/backlog.</summary>
    [HttpGet("template/backlog")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public IActionResult BacklogTemplate()
    {
        const string csv =
            "project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes,owner_team\r\n" +
            "CIB,EPIC-001,Admin & User Management,Administrator Login,US-001,Separate administrator login,Improved security and isolation of admin access.,System provides a dedicated login screen for administrators.,must_have,Feature,,MVP1,,R&D\r\n" +
            "OMS,EP-02,Identity Authentication & RBAC,,US-010,Invite users with specific roles,User management and login across all surfaces.,,must_have,Feature,,MVP1,,Product\r\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "pulse_backlog_template.csv");
    }

    /// <summary>
    /// Bulk-imports a product backlog from a CSV file (max 1000 rows). PMO only.
    /// Projects and epics are auto-created if they do not exist.
    /// Columns: project_name, epic_ref, epic_name, feature, story_ref, title, description,
    ///          acceptance_criteria, priority, type, story_points, phase, notes.
    /// </summary>
    [HttpPost("backlog")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ImportBacklog(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", "No file uploaded."));

        List<ImportBacklogRow> rows;
        try
        {
            rows = ParseBacklogCsv(file.OpenReadStream());
        }
        catch (FormatException ex)
        {
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", ex.Message));
        }

        return (await _mediator.Send(new ImportBacklogCommand(rows, GetActorId(), GetIp()), ct)).ToActionResult();
    }

    /// <summary>Returns a CSV template for bulk-importing users.</summary>
    [HttpGet("template/users")]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public IActionResult UsersTemplate()
    {
        const string csv =
            "name,email,role,team,baseline_points,baseline_cycle_days\r\n" +
            "Alice Johnson,alice@company.com,head_of_rd,R&D,10,14\r\n" +
            "Bob Smith,bob@company.com,engineer,Engineering,8,14\r\n" +
            "Carol Williams,carol@company.com,product_manager,Product,10,14\r\n";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "pulse_users_template.csv");
    }

    /// <summary>
    /// Bulk-imports users from a CSV file (max 200 rows) and sends each an invite email.
    /// Columns: name, email, role, team (required except for Executive, which has no team by
    /// design), baseline_points (optional), baseline_cycle_days (optional).
    /// Duplicate emails are skipped with a row-level failure.
    /// </summary>
    [HttpPost("users")]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ImportUsers(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", "No file uploaded."));

        List<ImportUserRow> rows;
        try
        {
            rows = ParseUsersCsv(file.OpenReadStream());
        }
        catch (FormatException ex)
        {
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", ex.Message));
        }

        var callerRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? string.Empty;
        return (await _mediator.Send(new ImportUsersCommand(rows, GetActorId(), GetIp(), callerRole), ct)).ToActionResult();
    }

    // ── CSV parsing ───────────────────────────────────────────────────────────

    private static List<ImportProjectRow> ParseProjectsCsv(Stream stream)
    {
        var lines = ReadLines(stream);
        if (lines.Count < 2)
            throw new FormatException("CSV must contain a header row and at least one data row.");

        // Validate header
        var header = lines[0].Select(h => h.ToLowerInvariant().Trim()).ToArray();
        var nameIdx      = Array.IndexOf(header, "name");
        var descIdx      = Array.IndexOf(header, "description");
        var ownerTeamIdx = Array.IndexOf(header, "owner_team");
        if (nameIdx < 0) throw new FormatException("CSV header must contain a 'name' column.");

        string? Cell(string[] cols, int idx) =>
            idx >= 0 && idx < cols.Length && !string.IsNullOrWhiteSpace(cols[idx]) ? cols[idx].Trim() : null;

        var rows = new List<ImportProjectRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i];
            var name = nameIdx < cols.Length ? cols[nameIdx] : string.Empty;
            rows.Add(new ImportProjectRow(name, Cell(cols, descIdx), Cell(cols, ownerTeamIdx)));
        }
        return rows;
    }

    private static List<ImportTaskRow> ParseTasksCsv(Stream stream)
    {
        var lines = ReadLines(stream);
        if (lines.Count < 2)
            throw new FormatException("CSV must contain a header row and at least one data row.");

        var header = lines[0].Select(h => h.ToLowerInvariant()).ToArray();
        var projectIdx  = Array.IndexOf(header, "project_name");
        var titleIdx    = Array.IndexOf(header, "title");
        var descIdx     = Array.IndexOf(header, "description");
        var acIdx       = Array.IndexOf(header, "acceptance_criteria");
        var pointsIdx   = Array.IndexOf(header, "points");
        var dueDateIdx  = Array.IndexOf(header, "due_date");
        var priorityIdx = Array.IndexOf(header, "priority");
        var typeIdx     = Array.IndexOf(header, "type");
        var assigneeIdx = Array.IndexOf(header, "assignee_email");
        var epicIdx     = Array.IndexOf(header, "epic_name");
        var extRefIdx   = Array.IndexOf(header, "external_reference");

        if (projectIdx < 0 || titleIdx < 0 || pointsIdx < 0 || dueDateIdx < 0)
            throw new FormatException("CSV header must contain: project_name, title, points, due_date.");

        var rows = new List<ImportTaskRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i];
            var projectName  = projectIdx < cols.Length  ? cols[projectIdx]  : string.Empty;
            var title        = titleIdx < cols.Length    ? cols[titleIdx]    : string.Empty;
            var desc         = descIdx >= 0 && descIdx < cols.Length ? cols[descIdx] : null;
            var ac           = acIdx >= 0 && acIdx < cols.Length ? cols[acIdx] : null;
            var pointsStr    = pointsIdx < cols.Length   ? cols[pointsIdx]   : "0";
            var dueDateStr   = dueDateIdx < cols.Length  ? cols[dueDateIdx]  : string.Empty;
            var priorityStr  = priorityIdx >= 0 && priorityIdx < cols.Length ? cols[priorityIdx] : null;
            var typeStr      = typeIdx >= 0 && typeIdx < cols.Length ? cols[typeIdx] : null;
            var assigneeEmail = assigneeIdx >= 0 && assigneeIdx < cols.Length ? cols[assigneeIdx] : null;
            var epicName      = epicIdx >= 0 && epicIdx < cols.Length ? cols[epicIdx] : null;
            var extRef        = extRefIdx >= 0 && extRefIdx < cols.Length ? cols[extRefIdx] : null;

            if (!int.TryParse(pointsStr, out var points))
                points = 0;

            // A missing/invalid due date is left null rather than defaulted — an invented
            // "today" reads as a real commitment and hides that the row needs grooming.
            DateOnly? dueDate = DateOnly.TryParse(dueDateStr, out var parsedDate) ? parsedDate : null;

            // Blank is left null (no priority set) rather than defaulted; an out-of-range value is
            // passed through as-is so ImportTasksHandler's own range check can report it as a
            // clear per-row failure instead of silently clamping or dropping it here.
            int? priority = int.TryParse(priorityStr, out var parsedPriority) ? parsedPriority : null;

            var type = Enum.TryParse<TaskType>(typeStr, ignoreCase: true, out var parsedType)
                ? parsedType
                : TaskType.Feature;

            rows.Add(new ImportTaskRow(
                projectName,
                title,
                string.IsNullOrWhiteSpace(desc) ? null : desc,
                string.IsNullOrWhiteSpace(ac) ? null : ac,
                points,
                dueDate,
                type,
                string.IsNullOrWhiteSpace(assigneeEmail) ? null : assigneeEmail,
                string.IsNullOrWhiteSpace(epicName) ? null : epicName,
                priority,
                string.IsNullOrWhiteSpace(extRef) ? null : extRef));
        }
        return rows;
    }

    private static List<ImportBacklogRow> ParseBacklogCsv(Stream stream)
    {
        var lines = ReadLines(stream);
        if (lines.Count < 2)
            throw new FormatException("CSV must contain a header row and at least one data row.");

        var header = lines[0].Select(h => h.ToLowerInvariant().Trim()).ToArray();
        int Col(string name) => Array.IndexOf(header, name);

        var projectIdx    = Col("project_name");
        var epicRefIdx    = Col("epic_ref");
        var epicNameIdx   = Col("epic_name");
        var featureIdx    = Col("feature");
        var storyRefIdx   = Col("story_ref");
        var titleIdx      = Col("title");
        var descIdx       = Col("description");
        var acIdx         = Col("acceptance_criteria");
        var priorityIdx   = Col("priority");
        var typeIdx       = Col("type");
        var pointsIdx     = Col("story_points");
        var phaseIdx      = Col("phase");
        var notesIdx      = Col("notes");
        var ownerTeamIdx  = Col("owner_team");

        if (projectIdx < 0 || epicNameIdx < 0 || titleIdx < 0)
            throw new FormatException("CSV header must contain: project_name, epic_name, title.");

        string? Cell(string[] cols, int idx) =>
            idx >= 0 && idx < cols.Length && !string.IsNullOrWhiteSpace(cols[idx]) ? cols[idx].Trim() : null;

        var rows = new List<ImportBacklogRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i];
            var pointsStr = pointsIdx >= 0 && pointsIdx < cols.Length ? cols[pointsIdx] : null;
            int? points = int.TryParse(pointsStr, out var p) ? p : null;

            rows.Add(new ImportBacklogRow(
                ProjectName:        projectIdx < cols.Length ? cols[projectIdx].Trim() : string.Empty,
                EpicRef:            Cell(cols, epicRefIdx),
                EpicName:           Cell(cols, epicNameIdx) ?? string.Empty,
                Feature:            Cell(cols, featureIdx),
                StoryRef:           Cell(cols, storyRefIdx),
                Title:              Cell(cols, titleIdx) ?? string.Empty,
                Description:        Cell(cols, descIdx),
                AcceptanceCriteria: Cell(cols, acIdx),
                Priority:           Cell(cols, priorityIdx),
                Type:               Cell(cols, typeIdx),
                StoryPoints:        points,
                Phase:              Cell(cols, phaseIdx),
                Notes:              Cell(cols, notesIdx),
                OwnerTeam:          Cell(cols, ownerTeamIdx)));
        }
        return rows;
    }

    private static List<ImportUserRow> ParseUsersCsv(Stream stream)
    {
        var lines = ReadLines(stream);
        if (lines.Count < 2)
            throw new FormatException("CSV must contain a header row and at least one data row.");

        var header = lines[0].Select(h => h.ToLowerInvariant().Trim()).ToArray();
        int Col(string name) => Array.IndexOf(header, name);

        var nameIdx     = Col("name");
        var emailIdx    = Col("email");
        var roleIdx     = Col("role");
        var teamIdx     = Col("team");
        var pointsIdx   = Col("baseline_points");
        var cycleDaysIdx = Col("baseline_cycle_days");

        if (nameIdx < 0 || emailIdx < 0 || roleIdx < 0)
            throw new FormatException("CSV header must contain: name, email, role.");

        string? Cell(string[] cols, int idx) =>
            idx >= 0 && idx < cols.Length && !string.IsNullOrWhiteSpace(cols[idx]) ? cols[idx].Trim() : null;

        var rows = new List<ImportUserRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i];
            var pointsStr   = pointsIdx >= 0 && pointsIdx < cols.Length ? cols[pointsIdx] : null;
            var cycleDaysStr = cycleDaysIdx >= 0 && cycleDaysIdx < cols.Length ? cols[cycleDaysIdx] : null;

            rows.Add(new ImportUserRow(
                Name:             Cell(cols, nameIdx)  ?? string.Empty,
                Email:            Cell(cols, emailIdx) ?? string.Empty,
                Role:             Cell(cols, roleIdx)  ?? string.Empty,
                Team:             Cell(cols, teamIdx),
                BaselinePoints:   int.TryParse(pointsStr,   out var pts)  ? pts  : null,
                BaselineCycleDays: int.TryParse(cycleDaysStr, out var days) ? days : null));
        }
        return rows;
    }

    // RFC-4180-style parser: a quoted field may span multiple physical lines and
    // contain commas, so rows must be split by scanning quote state across the
    // whole stream rather than line-by-line (which mangles multi-line descriptions).
    private static List<string[]> ReadLines(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField() { fields.Add(field.ToString().Trim()); field.Clear(); }
        void EndRow()
        {
            EndField();
            if (fields.Any(f => f.Length > 0))
                rows.Add(fields.ToArray());
            fields.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                if (c == '"') { inQuotes = false; i++; continue; }
                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; i++; break;
                case ',': EndField(); i++; break;
                case '\r': i++; break;
                case '\n': EndRow(); i++; break;
                default: field.Append(c); i++; break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRow();

        return rows;
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
