using System.Security.Claims;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Wiki;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Api.Security;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Application.Projects.Commands;
using Pulse.Application.Projects.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects/{projectId:guid}/wiki")]
[Tags("Wiki")]
[EngineerAuth]
public class WikiController : ControllerBase
{
    private readonly IMediator _mediator;

    public WikiController(IMediator mediator) => _mediator = mediator;

    public record CreateWikiPageRequest(string Title, string Content, bool RestrictedToMembers = false);
    public record UpdateWikiPageRequest(string Title, string Content, bool? RestrictedToMembers = null);

    /// <summary>Lists wiki pages for a project, alphabetically by title.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<WikiPageSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListPages(Guid projectId, [FromQuery] int limit = 25, [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new ListWikiPagesQuery(projectId, GetActorId(), GetRole(), limit, cursor))).ToActionResult();

    /// <summary>Returns a single wiki page. Open to every signed-in user unless it is restricted to the project's members.</summary>
    [HttpGet("{pageId:guid}", Name = "GetWikiPage")]
    [ProducesResponseType(typeof(ApiResponse<WikiPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetPage(Guid projectId, Guid pageId)
    {
        UseServiceRoleForOpenWikiRead();
        return GetPageCore(projectId, pageId);
    }

    private async Task<IActionResult> GetPageCore(Guid projectId, Guid pageId) =>
        (await _mediator.Send(new GetWikiPageQuery(projectId, pageId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Downloads a wiki page as a PDF — same access rule as viewing it.</summary>
    [HttpGet("{pageId:guid}/pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPagePdf(Guid projectId, Guid pageId)
    {
        UseServiceRoleForOpenWikiRead();
        var result = await _mediator.Send(new GetWikiPageQuery(projectId, pageId, GetActorId(), GetRole()));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var pdf = WikiPagePdfRenderer.Render(result.Data!.Title, result.Data.Content);
        var slug = Regex.Replace(result.Data.Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return File(pdf, "application/pdf", $"{(string.IsNullOrEmpty(slug) ? "wiki-page" : slug)}.pdf");
    }

    /// <summary>Creates a new wiki page.</summary>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<WikiPageDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePage(Guid projectId, [FromBody] CreateWikiPageRequest request)
    {
        var result = await _mediator.Send(new CreateWikiPageCommand(
            projectId, request.Title, request.Content, GetActorId(), GetRole(), request.RestrictedToMembers));
        return result.ToCreatedResult("GetWikiPage", new { projectId, pageId = result.Data?.Id });
    }

    /// <summary>Updates a wiki page's title and content.</summary>
    [HttpPut("{pageId:guid}")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<WikiPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdatePage(Guid projectId, Guid pageId, [FromBody] UpdateWikiPageRequest request) =>
        (await _mediator.Send(new UpdateWikiPageCommand(
            projectId, pageId, request.Title, request.Content, GetActorId(), GetRole(), request.RestrictedToMembers))).ToActionResult();

    /// <summary>Permanently deletes a wiki page.</summary>
    [HttpDelete("{pageId:guid}")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeletePage(Guid projectId, Guid pageId)
    {
        var result = await _mediator.Send(new DeleteWikiPageCommand(projectId, pageId, GetActorId(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>Lists all saved revisions for a wiki page, newest first.</summary>
    [HttpGet("{pageId:guid}/revisions")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WikiPageRevisionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListRevisions(Guid projectId, Guid pageId) =>
        (await _mediator.Send(new ListWikiRevisionsQuery(projectId, pageId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns the full content of a specific revision.</summary>
    [HttpGet("{pageId:guid}/revisions/{revisionId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<WikiPageRevisionContentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRevision(Guid projectId, Guid pageId, Guid revisionId) =>
        (await _mediator.Send(new GetWikiRevisionQuery(projectId, pageId, revisionId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>A wiki page is readable by every signed-in user unless its author restricted it to the project's
    /// members. Row-level security is keyed on project membership, so it would hide a non-member's page; this
    /// read-only request opts out of it (see <see cref="RlsServiceOverride"/>) and the handler applies the
    /// "restricted to members" rule itself.</summary>
    private void UseServiceRoleForOpenWikiRead() => RlsServiceOverride.Apply(HttpContext);

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
