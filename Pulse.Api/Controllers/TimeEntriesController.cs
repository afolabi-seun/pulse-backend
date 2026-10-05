using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Reports;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.TimeEntries;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.TimeEntries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/time-entries")]
[Tags("Time entries")]
[EngineerAuth]
public class TimeEntriesController : ControllerBase
{
    private readonly IMediator _mediator;

    public TimeEntriesController(IMediator mediator) => _mediator = mediator;

    public record LogTimeEntryRequest(DateOnly Date, string Category, Guid? TaskId, Guid? ProjectId, decimal Hours, string? Note);
    public record UpdateTimeEntryRequest(DateOnly Date, string Category, Guid? TaskId, Guid? ProjectId, decimal Hours, string? Note);
    public record StartTimerRequest(string Category, Guid? TaskId, int? Points, Guid? SubtaskId = null);

    /// <summary>Returns time entries for an engineer.</summary>
    /// <remarks>
    /// Engineers always see their own entries only. Team leads and above may pass engineerId to
    /// view another engineer's entries. Pass from/to for a date-range view (e.g. the current
    /// week); omit them for cursor-paginated history, ordered by date descending.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<TimeEntryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListTimeEntries(
        [FromQuery] Guid? engineerId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new ListTimeEntriesQuery(
            GetActorId(), GetRole(), engineerId, from, to, limit, cursor))).ToActionResult();

    /// <summary>Returns what an engineer is assigned to next to what they logged time on, for a period.</summary>
    /// <remarks>
    /// For the Time Summary drill-down: every task currently assigned to them (with the hours logged on it —
    /// zero when none), the tasks they logged time on that are no longer assigned to them, and their
    /// meeting/admin/leave/other time. Same visibility as <c>GET /time-entries?engineerId=</c>: PMO and
    /// Executive/HR/Accountant see everyone, department heads their department, team leads their team.
    /// </remarks>
    [HttpGet("engineer-activity")]
    [ProducesResponseType(typeof(ApiResponse<EngineerTimeActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEngineerActivity(
        [FromQuery] Guid engineerId, [FromQuery] DateOnly from, [FromQuery] DateOnly to) =>
        (await _mediator.Send(new GetEngineerTimeActivityQuery(GetActorId(), GetRole(), engineerId, from, to))).ToActionResult();

    /// <summary>Returns who logged the hours behind one "Hours by project" line of the Time Summary, and on what.</summary>
    /// <remarks>
    /// <c>kind=project</c> (with <c>projectId</c>), <c>kind=general</c> (meetings, admin, leave and other time with no
    /// project) or <c>kind=personal</c> (everyone's personal tasks — people and hours only, never titles). Covers the same
    /// engineers as <c>GET /time-entries/summary</c>, so the rows add up to the line they were opened from.
    /// </remarks>
    [HttpGet("project-activity")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<ProjectTimeActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetProjectActivity(
        [FromQuery] string kind, [FromQuery] Guid? projectId, [FromQuery] DateOnly from, [FromQuery] DateOnly to) =>
        (await _mediator.Send(new GetProjectTimeActivityQuery(GetActorId(), GetRole(), kind, projectId, from, to))).ToActionResult();

    /// <summary>Returns a single time entry by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetTimeEntry")]
    [ProducesResponseType(typeof(ApiResponse<TimeEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTimeEntry(Guid id) =>
        (await _mediator.Send(new GetTimeEntryQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Logs a new time entry for the authenticated engineer.</summary>
    /// <remarks>Not available to PMO (Head of PMO, Project Manager) or Executive roles.</remarks>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<TimeEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LogTimeEntry([FromBody] LogTimeEntryRequest request)
    {
        var actorId = GetActorId();
        var category = Enum.Parse<TimeEntryCategory>(request.Category, ignoreCase: true);
        var result = await _mediator.Send(new LogTimeEntryCommand(
            actorId, request.Date, category, request.TaskId, request.ProjectId, request.Hours, request.Note, actorId, GetIp()));
        return result.ToActionResult();
    }

    /// <summary>Updates an existing time entry. Own entries only.</summary>
    [HttpPut("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<TimeEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateTimeEntry(Guid id, [FromBody] UpdateTimeEntryRequest request)
    {
        var category = Enum.Parse<TimeEntryCategory>(request.Category, ignoreCase: true);
        var result = await _mediator.Send(new UpdateTimeEntryCommand(
            id, request.Date, category, request.TaskId, request.ProjectId, request.Hours, request.Note, GetActorId(), GetIp()));
        return result.ToActionResult();
    }

    /// <summary>Deletes a time entry. Own entries only.</summary>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteTimeEntry(Guid id) =>
        (await _mediator.Send(new DeleteTimeEntryCommand(id, GetActorId(), GetIp()))).ToActionResult();

    /// <summary>Returns an hours-logged roll-up, scoped to the caller's visibility.</summary>
    /// <remarks>
    /// PMO/Product head/Executive see all engineers. Dept heads see their department. Team leads
    /// see their own team. Pass weekOf for the default Monday-Sunday week, or from/to together for
    /// an arbitrary date range (up to 62 days) — from/to takes precedence when both are given.
    /// </remarks>
    [HttpGet("summary")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<TimeEntrySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSummary([FromQuery] DateOnly? weekOf, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to) =>
        (await _mediator.Send(new GetTimeEntrySummaryQuery(weekOf, GetRole(), GetActorId(), from, to))).ToActionResult();

    /// <summary>Downloads the hours-logged roll-up as a CSV file.</summary>
    /// <remarks>Same access and scoping as the JSON endpoint above.</remarks>
    [HttpGet("summary/csv")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSummaryCsv([FromQuery] DateOnly? weekOf, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var result = await _mediator.Send(new GetTimeEntrySummaryQuery(weekOf, GetRole(), GetActorId(), from, to));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var csv = TimeEntrySummaryCsvRenderer.Render(result.Data!);
        var filename = $"time-summary-{result.Data!.WeekOf}_{result.Data!.To}.csv";
        return File(csv, "text/csv", filename);
    }

    /// <summary>Returns total hours logged against a task, alongside its point-derived estimate.</summary>
    [HttpGet("task-summary/{taskId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TaskTimeSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTaskSummary(Guid taskId) =>
        (await _mediator.Send(new GetTaskTimeSummaryQuery(taskId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Starts a timer for the authenticated engineer, stopping-and-logging any timer already running.</summary>
    /// <remarks>
    /// Category Task requires TaskId. Starting a timer on an unassigned task self-assigns it to the
    /// caller; a task already assigned to someone else returns FORBIDDEN. An ungroomed Backlog task
    /// (no points) returns TASK_MISSING_POINTS — retry the same request with Points set to proceed.
    /// SubtaskId times a specific checklist item loaned to the caller instead of the task as a
    /// whole — it does not require the caller to own the parent task, only the subtask.
    /// </remarks>
    [HttpPost("timer/start")]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<ActiveTimerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartTimer([FromBody] StartTimerRequest request)
    {
        var actorId = GetActorId();
        var category = Enum.Parse<TimeEntryCategory>(request.Category, ignoreCase: true);
        var result = await _mediator.Send(new StartTimerCommand(
            actorId, GetRole(), category, request.TaskId, request.Points, actorId, GetIp(), request.SubtaskId));
        return result.ToActionResult();
    }

    /// <summary>Stops the authenticated engineer's currently running timer and logs it as a time entry.</summary>
    [HttpPost("timer/stop")]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<TimeEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StopTimer()
    {
        var actorId = GetActorId();
        var result = await _mediator.Send(new StopTimerCommand(actorId, actorId, GetIp()));
        return result.ToActionResult();
    }

    /// <summary>Returns the authenticated engineer's currently running timer, or null if none.</summary>
    [HttpGet("timer/active")]
    [RequiresCapability(CapabilityRegistry.TimeEntrySubmitter)]
    [ProducesResponseType(typeof(ApiResponse<ActiveTimerDto?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActiveTimer() =>
        (await _mediator.Send(new GetActiveTimerQuery(GetActorId()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
