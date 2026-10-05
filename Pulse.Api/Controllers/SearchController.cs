using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Search;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/search")]
[Tags("Search")]
[EngineerAuth]
public class SearchController : ControllerBase
{
    private readonly IMediator _mediator;

    public SearchController(IMediator mediator) => _mediator = mediator;

    /// <summary>Full-text search across tasks and engineers.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<SearchResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct) =>
        (await _mediator.Send(new SearchQuery(q ?? string.Empty,
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty), ct)).ToActionResult();
}
