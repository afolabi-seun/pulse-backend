using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Engineers;
using Pulse.Application.Engineers.Commands;
using Pulse.Application.Engineers.Queries;
using Pulse.Application.Overrides;
using Pulse.Application.Overrides.Commands;
using Pulse.Application.Overrides.Queries;
using Pulse.Application.Overwork;
using Pulse.Application.Overwork.Queries;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/engineers")]
[Tags("Engineers")]
public class EngineersController : ControllerBase
{
    private readonly IMediator _mediator;

    public EngineersController(IMediator mediator) => _mediator = mediator;

    public record UpdateBaselineRequest(int BaselinePoints, int BaselineCycleDays);

    /// <summary>Returns engineers with workload data, scoped to the caller's department or team.
    /// PM+, department heads, and Executive see broadly (org-wide or department-wide); a team lead
    /// sees only their own team — see ListEngineersHandler's role branches for the exact scoping.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListEngineers()
    {
        var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        return (await _mediator.Send(new ListEngineersQuery(callerRole, callerId))).ToActionResult();
    }

    /// <summary>Paged, row-filterable variant of ListEngineers — backs the Engineers page's own grid.
    /// Stats in the response reflect the full role-scoped roster regardless of the team/isActive
    /// filters, matching the page's stat tiles staying department-wide even when the grid is narrowed.</summary>
    [HttpGet("page")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<EngineerListPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListEngineersPage(
        [FromQuery] int limit = 24, [FromQuery] string? cursor = null,
        [FromQuery] Guid? teamId = null, [FromQuery] bool? isActive = null)
    {
        var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        return (await _mediator.Send(new ListEngineersPageQuery(callerRole, callerId, limit, cursor, teamId, isActive))).ToActionResult();
    }

    /// <summary>Returns active engineers outside the caller's department — valid loan targets.</summary>
    /// <remarks>Backs the Loan Task picker: a team lead may only loan a task to an engineer outside
    /// their own department (see LoanTaskCommand), so the picker needs exactly that set rather than
    /// the team-scoped list ListEngineers returns.</remarks>
    [HttpGet("loan-candidates")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrHeadOnly)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLoanCandidates() =>
        (await _mediator.Send(new GetLoanCandidatesQuery(GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns active, QA-flagged engineers org-wide — valid targets for assigning a
    /// QA review task.</summary>
    /// <remarks>Backs the QA task Assignee picker. Deliberately unscoped by department, unlike
    /// ListEngineers: a department head editing a QA task (e.g. Head of Engineering) still needs
    /// to see a QA reviewer in another department (e.g. Product), the same reasoning
    /// loan-candidates already applies to loan targets.</remarks>
    [HttpGet("qa-candidates")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetQaCandidates() =>
        (await _mediator.Send(new GetQaCandidatesQuery())).ToActionResult();

    /// <summary>Returns active Team-Lead-or-above engineers org-wide — valid targets for
    /// reassigning a pending PR approval request.</summary>
    /// <remarks>Backs the PR-approval "reassign approver" picker: the whole point is picking
    /// someone other than the unavailable default department head, so this is deliberately
    /// unscoped by department, the same reasoning loan-candidates/qa-candidates already apply.</remarks>
    [HttpGet("pr-approval-candidates")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPrApprovalCandidates() =>
        (await _mediator.Send(new GetPrApprovalCandidatesQuery())).ToActionResult();

    /// <summary>Returns active engineers who actually work on the given project — its owner
    /// team's roster plus explicit project members — regardless of the caller's own department.</summary>
    /// <remarks>Backs the task Assignee picker for a normal task, the same way qa-candidates backs
    /// the QA task one: a department head managing a project outside their own department still
    /// needs to see that project's real team, not their own department's roster.</remarks>
    [HttpGet("project-assignable/{projectId:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove, CapabilityRegistry.ExecutiveRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProjectAssignableEngineers(Guid projectId) =>
        (await _mediator.Send(new GetProjectAssignableEngineersQuery(projectId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns a single active engineer by ID.</summary>
    /// <remarks>PM and above may query any engineer; a team lead may query members of their own
    /// team. Everyone else may only query themselves — the dashboard, task list, and performance
    /// pages all fetch the caller's own record.</remarks>
    [HttpGet("{id:guid}")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<EngineerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEngineer(Guid id) =>
        (await _mediator.Send(new GetEngineerQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns delivered story points per week for the engineer over the 6-week rolling window.</summary>
    /// <remarks>Same access as GetEngineer: self, PM and above, or a team lead viewing their own team.</remarks>
    [HttpGet("{id:guid}/throughput")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ThroughputWeekDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEngineerThroughput(Guid id) =>
        (await _mediator.Send(new GetEngineerThroughputQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns the current overwork signals for an engineer.</summary>
    /// <remarks>PM and above may query any engineer. Engineers may only query themselves.</remarks>
    [HttpGet("{id:guid}/signals")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<OverworkSignalsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSignals(Guid id)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

        // Engineers can only see their own signals
        if (role == Domain.Engineers.Roles.Engineer && id != actorId)
            return Forbid();

        return (await _mediator.Send(new GetEngineerSignalsQuery(id))).ToActionResult();
    }

    /// <summary>Updates an engineer's workload baseline (points and cycle days).</summary>
    /// <remarks>
    /// Baseline changes are audit-logged because they directly affect overwork signal thresholds.
    /// Any department head, or PMO (Head of PMO / Project Manager), may change baselines.
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<EngineerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateBaseline(Guid id, [FromBody] UpdateBaselineRequest request) =>
        (await _mediator.Send(new UpdateEngineerBaselineCommand(
            id, request.BaselinePoints, request.BaselineCycleDays,
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            HttpContext.Connection.RemoteIpAddress?.ToString()))).ToActionResult();

    /// <summary>Returns the baseline calibration history for an engineer.</summary>
    /// <remarks>
    /// History is derived from audit log entries written when baselines are changed.
    /// Any department head, or PMO (Head of PMO / Project Manager).
    /// </remarks>
    [HttpGet("{id:guid}/baseline-history")]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BaselineHistoryEntryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetBaselineHistory(Guid id) =>
        (await _mediator.Send(new GetBaselineHistoryQuery(id,
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty))).ToActionResult();

    public record CreateOverrideRequest(string Reason, DateTime? ExpiresAt);

    /// <summary>Creates an overwork override for an engineer, suppressing the overwork flag for a set period.</summary>
    /// <remarks>
    /// Engineers may only create overrides for themselves. PMs and above may create overrides for any engineer.
    /// Defaults to 7 days if ExpiresAt is not provided.
    /// </remarks>
    [HttpPost("{id:guid}/override")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<OverrideDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateOverride(Guid id, [FromBody] CreateOverrideRequest request)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

        // Self, or PM+ — matches this endpoint's own doc comment. Anyone else (HR, Executive,
        // Designer, a Team Lead acting on someone outside their own scope, ...) was previously
        // let through by accident: the old check only ever blocked a plain Engineer targeting
        // someone else, so every other role fell through unrestricted.
        if (id != actorId && !CapabilityRegistry.ResolveFor(role).Contains(CapabilityRegistry.PmOrAbove))
            return Forbid();

        var result = await _mediator.Send(new CreateOverrideCommand(
            id, request.Reason, request.ExpiresAt, actorId,
            HttpContext.Connection.RemoteIpAddress?.ToString()));

        return result.ToCreatedResult("GetEngineerOverrides", new { id });
    }

    /// <summary>Returns the override history for an engineer.</summary>
    /// <remarks>Engineers may only view their own override history. Head of R&amp;D may view any engineer's history.</remarks>
    [HttpGet("{id:guid}/overrides", Name = "GetEngineerOverrides")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OverrideDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListOverrides(Guid id)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

        if (role == Roles.Engineer && id != actorId)
            return Forbid();

        return (await _mediator.Send(new ListOverridesQuery(id))).ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
