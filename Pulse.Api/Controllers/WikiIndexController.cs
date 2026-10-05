using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Api.Security;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Application.Projects.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/wiki")]
[Tags("Wiki")]
[EngineerAuth]
public class WikiIndexController : ControllerBase
{
    private readonly IMediator _mediator;

    public WikiIndexController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns every wiki page the caller may read — all of them except pages restricted to a project's members
    /// that the caller isn't part of — ordered by project then page title, paginated.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<WikiIndexEntryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAll([FromQuery] int limit = 25, [FromQuery] string? cursor = null)
    {
        // Read-only, and the handler hides restricted pages from non-members — see WikiController.
        RlsServiceOverride.Apply(HttpContext);
        return (await _mediator.Send(new ListAllWikiPagesQuery(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            limit, cursor))).ToActionResult();
    }
}
