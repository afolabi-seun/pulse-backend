using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Organizations;
using Pulse.Application.Organizations.Commands;
using Pulse.Application.Organizations.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>
/// Operator-only administration across organizations (multi-tenancy Phase 2a). Protected by
/// <see cref="OperatorAuthAttribute"/> — not reachable with a user login, and absent (404) unless
/// OPERATOR_API_KEY is configured. Hidden from Swagger.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/operator")]
[OperatorAuth]
[ApiExplorerSettings(IgnoreApi = true)]
public class OperatorController(IMediator mediator) : ControllerBase
{
    public record CreateOrganizationRequest(string Name, string Slug, string AdminName, string AdminEmail, string? BillingEmail);

    /// <summary>Creates an organization and emails its first Head an activation link.</summary>
    [HttpPost("organizations")]
    [ProducesResponseType(typeof(ApiResponse<CreatedOrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOrganization([FromBody] CreateOrganizationRequest request, CancellationToken ct) =>
        (await mediator.Send(new CreateOrganizationCommand(request.Name, request.Slug, request.AdminName,
            request.AdminEmail, request.BillingEmail, HttpContext.Connection.RemoteIpAddress?.ToString()), ct)).ToActionResult();

    /// <summary>Lists every organization with its engineer count.</summary>
    [HttpGet("organizations")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OrganizationDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOrganizations(CancellationToken ct) =>
        (await mediator.Send(new ListOrganizationsQuery(), ct)).ToActionResult();
}
