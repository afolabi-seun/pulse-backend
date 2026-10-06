using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Organizations.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>The signed-in user's own organization.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/organization")]
[EngineerAuth]
public class OrganizationController(IMediator mediator) : ControllerBase
{
    /// <summary>Returns the caller's organization (id, name, slug).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<CurrentOrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrent(CancellationToken ct) =>
        (await mediator.Send(new GetCurrentOrganizationQuery(), ct)).ToActionResult();
}
