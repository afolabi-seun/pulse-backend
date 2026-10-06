using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Notifications.Commands;
using Pulse.Application.Notifications.Queries;
using Pulse.Application.Notifications.Preferences;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
[Tags("Notifications")]
[EngineerAuth]
public class NotificationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public NotificationsController(IMediator mediator) => _mediator = mediator;

    public record CreateNotificationRequest(Guid UserId, string Kind, string? Payload, string Channel);

    /// <summary>Returns the authenticated user's in-app notification inbox.</summary>
    /// <remarks>Pass unreadOnly=true to filter to unread notifications only. Results newest-first.</remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<NotificationDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new ListNotificationsQuery(GetActorId(), unreadOnly, limit, cursor))).ToActionResult();

    /// <summary>Marks a notification as read.</summary>
    [HttpPatch("{id:guid}/read")]
    [ProducesResponseType(typeof(ApiResponse<NotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkRead(Guid id) =>
        (await _mediator.Send(new MarkNotificationReadCommand(id, GetActorId()))).ToActionResult();

    /// <summary>Marks all of the authenticated user's unread notifications as read.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllRead()
    {
        await _mediator.Send(new MarkAllNotificationsReadCommand(GetActorId()));
        return NoContent();
    }

    /// <summary>One-time cleanup of EscalationOverdue notifications created before the
    /// overdue-audience scoping fix (see EscalationScanner) — deletes any whose recipient no
    /// longer (or never did) have access to the notification's underlying task. Safe to call
    /// more than once; a second run reports 0 deleted.</summary>
    [HttpPost("cleanup-orphaned-escalations")]
    [RequiresCapability(CapabilityRegistry.HeadOfPmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<CleanupSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CleanupOrphanedEscalations() =>
        (await _mediator.Send(new CleanupOrphanedEscalationNotificationsCommand())).ToActionResult();

    /// <summary>Either or both; an omitted setting is left as it is.</summary>
    public record UpdatePreferenceRequest(bool? Email, bool? Chat);
    public record UpdateChatChannelRequest(string Channel);

    /// <summary>Where the caller's notifications are also sent as personal chat messages, and what's available.</summary>
    [HttpGet("chat")]
    [ProducesResponseType(typeof(ApiResponse<PersonalChatSettingsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChatSettings(CancellationToken ct) =>
        (await _mediator.Send(new GetPersonalChatSettingsQuery(GetActorId()), ct)).ToActionResult();

    /// <summary>Chooses the caller's personal chat channel: none, slack or google_chat.</summary>
    [HttpPut("chat")]
    [ProducesResponseType(typeof(ApiResponse<PersonalChatSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateChatChannel([FromBody] UpdateChatChannelRequest request, CancellationToken ct) =>
        (await _mediator.Send(new UpdatePersonalChatChannelCommand(GetActorId(), request.Channel), ct)).ToActionResult();

    /// <summary>The caller's notification preferences: every kind, and whether it's emailed to them.</summary>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<NotificationPreferenceDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPreferences(CancellationToken ct) =>
        (await _mediator.Send(new GetNotificationPreferencesQuery(GetActorId()), ct)).ToActionResult();

    /// <summary>Turns email on or off for one notification kind (security notices stay on).</summary>
    [HttpPut("preferences/{kind}")]
    [ProducesResponseType(typeof(ApiResponse<NotificationPreferenceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdatePreference(string kind, [FromBody] UpdatePreferenceRequest request, CancellationToken ct) =>
        (await _mediator.Send(new UpdateNotificationPreferenceCommand(GetActorId(), kind, request.Email, request.Chat), ct)).ToActionResult();

    /// <summary>Creates a notification. Internal use only — called by background jobs and event handlers.</summary>
    [HttpPost]
    [ServiceAuth]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(ApiResponse<NotificationDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateNotification([FromBody] CreateNotificationRequest request)
    {
        var result = await _mediator.Send(new CreateNotificationCommand(
            request.UserId, request.Kind, request.Payload, request.Channel));

        return result.ToCreatedResult("GetNotification", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single notification by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetNotification")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(ApiResponse<NotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetNotification(Guid id) =>
        (await _mediator.Send(new GetNotificationQuery(id, GetActorId()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
