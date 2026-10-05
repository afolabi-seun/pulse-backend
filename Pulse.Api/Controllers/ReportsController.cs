using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Api.Reports;
using Pulse.Application.Common;
using Pulse.Application.Overwork;
using Pulse.Application.Overwork.Queries;
using Pulse.Application.Reports;
using Pulse.Application.Reports.Commands;
using Pulse.Application.Reports.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
[Tags("Reports")]
[EngineerAuth]
public class ReportsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ReportsController(IMediator mediator) => _mediator = mediator;

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    /// <summary>Returns the authenticated engineer's own personalized report — their current overwork signals.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<OverworkSignalsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyReport() =>
        (await _mediator.Send(new GetEngineerSignalsQuery(GetActorId()))).ToActionResult();

    /// <summary>Returns the weekly leadership report — engineer workloads, escalations, and blockers.</summary>
    /// <remarks>Accessible to any department head (their own department), plus Executive and HR
    /// (org-wide, matching their other reporting queries). Pass ?weekOf=yyyy-MM-dd to retrieve a
    /// historical week.</remarks>
    [HttpGet("leadership")]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<LeadershipReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLeadershipReport([FromQuery] DateOnly? weekOf = null) =>
        (await _mediator.Send(new GetLeadershipReportQuery(GetActorId(), GetRole(), weekOf))).ToActionResult();

    /// <summary>Downloads the weekly leadership report as a PDF.</summary>
    /// <remarks>Same access as the JSON endpoint above. Pass ?weekOf=yyyy-MM-dd to retrieve a historical week.</remarks>
    [HttpGet("leadership/pdf")]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLeadershipReportPdf([FromQuery] DateOnly? weekOf = null)
    {
        var result = await _mediator.Send(new GetLeadershipReportQuery(GetActorId(), GetRole(), weekOf));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var pdf = LeadershipReportPdfRenderer.Render(result.Data!);
        var filename = $"leadership-report-{result.Data!.WeekOf}.pdf";
        return File(pdf, "application/pdf", filename);
    }

    /// <summary>Returns the consolidated PMO report — team utilization, project health, sprint velocity, check-in compliance, and blocker aging.</summary>
    /// <remarks>Accessible to team leads and above. Head of PMO sees all teams; others see only their own team. Pass ?weekOf=yyyy-MM-dd to retrieve a historical week.</remarks>
    [HttpGet("pmo")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<PmoReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPmoReport(
        [FromQuery] DateOnly? weekOf = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null) =>
        (await _mediator.Send(new GetPmoReportQuery(GetRole(), GetActorId(), weekOf, from, to))).ToActionResult();

    /// <summary>Downloads the consolidated PMO report as a CSV file.</summary>
    /// <remarks>Same access as the JSON endpoint above. Pass ?weekOf=yyyy-MM-dd to retrieve a historical week.</remarks>
    [HttpGet("pmo/csv")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPmoReportCsv(
        [FromQuery] DateOnly? weekOf = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null)
    {
        var result = await _mediator.Send(new GetPmoReportQuery(GetRole(), GetActorId(), weekOf, from, to));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var csv = PmoReportCsvRenderer.Render(result.Data!);
        var filename = weekOf.HasValue
            ? $"pmo-report-{weekOf.Value:yyyy-MM-dd}.csv"
            : $"pmo-report-{result.Data!.WeekOf}.csv";
        return File(csv, "text/csv", filename);
    }

    /// <summary>Returns org-wide delivered-points and escalation trends over the last 6 weeks —
    /// purpose-built for the Executive dashboard.</summary>
    [HttpGet("org-trend")]
    [RequiresCapability(CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<OrgTrendDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOrgTrend() =>
        (await _mediator.Send(new GetOrgTrendQuery())).ToActionResult();

    /// <summary>Returns the weekly team report — auto-computed workstream status, blockers, and KPIs, plus the team lead's saved narrative sections.</summary>
    /// <remarks>Accessible to team leads and above. Team leads see only their own team; Head of PMO/Product must pass teamId. Pass ?weekOf=yyyy-MM-dd to retrieve a historical week.</remarks>
    [HttpGet("weekly")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<WeeklyReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetWeeklyReport([FromQuery] Guid? teamId = null, [FromQuery] DateOnly? weekOf = null) =>
        (await _mediator.Send(new GetWeeklyReportQuery(GetActorId(), GetRole(), teamId, weekOf))).ToActionResult();

    /// <summary>Downloads the weekly team report as an editable .docx, filled into the Pulse Weekly Report Template.</summary>
    /// <remarks>Accessible to team leads and above. Team leads see only their own team; Head of PMO/Product must pass teamId.</remarks>
    [HttpGet("weekly/docx")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetWeeklyReportDocx([FromQuery] Guid? teamId = null, [FromQuery] DateOnly? weekOf = null)
    {
        var result = await _mediator.Send(new GetWeeklyReportQuery(GetActorId(), GetRole(), teamId, weekOf));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var docx = WeeklyReportDocxRenderer.Render(result.Data!);
        var filename = $"weekly-report-{result.Data!.TeamName}-{result.Data!.WeekOf}.docx";
        return File(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", filename);
    }

    /// <summary>Saves (creates or updates) the narrative sections of the caller's own team's weekly report draft.</summary>
    /// <remarks>Restricted to the team's own team lead. Editing an already-submitted report clears its sign-off.</remarks>
    [HttpPut("weekly/draft")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<WeeklyReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SaveWeeklyReportDraft([FromBody] SaveWeeklyReportDraftRequest request) =>
        (await _mediator.Send(new SaveWeeklyReportDraftCommand(
            GetActorId(), GetRole(), request.TeamId, request.WeekOf,
            request.ExecutiveSummary, request.KeyAccomplishments, request.PlannedNextWeek, request.ResourcingNotes))).ToActionResult();

    /// <summary>Signs off the caller's own team's weekly report for the given week.</summary>
    /// <remarks>Restricted to the team's own team lead. A draft must exist for that week first.</remarks>
    [HttpPost("weekly/submit")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<WeeklyReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitWeeklyReport([FromBody] SubmitWeeklyReportRequest request) =>
        (await _mediator.Send(new SubmitWeeklyReportCommand(GetActorId(), GetRole(), request.TeamId, request.WeekOf))).ToActionResult();
}

public record SaveWeeklyReportDraftRequest(
    Guid TeamId,
    DateOnly WeekOf,
    string ExecutiveSummary,
    string KeyAccomplishments,
    string PlannedNextWeek,
    string ResourcingNotes);

public record SubmitWeeklyReportRequest(Guid TeamId, DateOnly WeekOf);
