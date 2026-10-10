using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Organizations.Commands;
using Pulse.Application.Organizations.Queries;
using Pulse.Domain.Organizations;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>The signed-in user's own organization, and its branding.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/organization")]
[EngineerAuth]
public class OrganizationController(IMediator mediator) : ControllerBase
{
    public record UpdateBrandingRequest(string Name, string? BrandColor);

    /// <summary>Returns the caller's organization: id, name, slug, brand colour and logo version.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<CurrentOrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrent(CancellationToken ct) =>
        (await mediator.Send(new GetCurrentOrganizationQuery(), ct)).ToActionResult();

    /// <summary>Sets the organization's display name and accent colour (#RRGGBB, or null for Pulse's own).</summary>
    [HttpPut("branding")]
    [RequiresCapability(CapabilityRegistry.HeadOnly)]
    [ProducesResponseType(typeof(ApiResponse<CurrentOrganizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateBranding([FromBody] UpdateBrandingRequest request, CancellationToken ct) =>
        (await mediator.Send(new UpdateOrganizationBrandingCommand(request.Name, request.BrandColor, ActorId, Ip), ct)).ToActionResult();

    /// <summary>The organization's logo image. 404 when there isn't one.</summary>
    [HttpGet("logo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLogo(CancellationToken ct)
    {
        var result = await mediator.Send(new GetOrganizationLogoQuery(), ct);
        if (!result.IsSuccess)
            return NotFound();
        // Served as an image only, never sniffed into anything else; cached privately — the app refetches by logo version.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(result.Data!.Data, result.Data.ContentType);
    }

    /// <summary>Uploads the organization's logo: PNG, JPEG or WebP, 256 KB at most.</summary>
    [HttpPut("logo")]
    [RequiresCapability(CapabilityRegistry.HeadOnly)]
    [RequestSizeLimit(OrganizationLogo.MaxBytes + 16 * 1024)]
    [ProducesResponseType(typeof(ApiResponse<CurrentOrganizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetLogo(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > OrganizationLogo.MaxBytes)
            return BadRequest(ApiResponse<object>.Failure("VALIDATION_ERROR", $"Choose a PNG, JPEG or WebP image of at most {OrganizationLogo.MaxBytes / 1024} KB."));
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return (await mediator.Send(new SetOrganizationLogoCommand(file.ContentType, buffer.ToArray(), ActorId, Ip), ct)).ToActionResult();
    }

    /// <summary>Removes the organization's logo.</summary>
    [HttpDelete("logo")]
    [RequiresCapability(CapabilityRegistry.HeadOnly)]
    [ProducesResponseType(typeof(ApiResponse<CurrentOrganizationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct) =>
        (await mediator.Send(new RemoveOrganizationLogoCommand(ActorId, Ip), ct)).ToActionResult();

    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
}
