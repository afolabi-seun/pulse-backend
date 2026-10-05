using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Meta;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/meta")]
[Tags("Meta")]
[EngineerAuth]
public class MetaController : ControllerBase
{
    private readonly IMediator _mediator;

    public MetaController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns static application metadata: roles, task statuses, task types, and sprint statuses.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<AppMetaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMeta() =>
        (await _mediator.Send(new GetMetaQuery())).ToActionResult();
}
