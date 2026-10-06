using System.Text.Json.Nodes;
using Asp.Versioning;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.GoogleChat;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Pulse.Application.Integrations.GoogleChat;
using MediatR;

namespace Pulse.Api.Controllers;

/// <summary>Receives Google Chat's HTTP callbacks so a reply inside an alert's Chat thread can be
/// answered by IAlertExplainer. Called by Google's own servers, not a Pulse user — carries no
/// [EngineerAuth] (EngineerAuthAttribute is opt-in per controller, not a global filter), and is
/// authenticated instead by verifying the request's Google-signed bearer token (IGoogleChatRequestVerifier).
/// Never does any real work synchronously: a matching message is handed off to
/// HandleGoogleChatReplyJob and this returns immediately.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations/google-chat/events")]
[Tags("Google Chat")]
public class GoogleChatEventsController : ControllerBase
{
    private readonly IGoogleChatRequestVerifier _verifier;
    private readonly IGoogleChatSpaceRepository _spaces;
    private readonly IBackgroundJobClient _jobs;
    private readonly IMediator _mediator;

    // "@Pulse link ABCD-2345" — Chat strips the mention into argumentText.
    private static readonly Regex LinkCommand = new(@"^\s*link\s+([A-Za-z0-9]{4}-?[A-Za-z0-9]{4})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GoogleChatEventsController(
        IGoogleChatRequestVerifier verifier, IGoogleChatSpaceRepository spaces, IBackgroundJobClient jobs, IMediator mediator)
    {
        _verifier = verifier;
        _spaces = spaces;
        _jobs = jobs;
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] JsonObject body, CancellationToken ct)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        var bearerToken = authHeader.StartsWith("Bearer ", StringComparison.Ordinal) ? authHeader["Bearer ".Length..] : null;
        if (!await _verifier.VerifyAsync(bearerToken, ct))
            return Unauthorized();

        var type = body["type"]?.GetValue<string>();

        if (type == "ADDED_TO_SPACE" && body["space"] is JsonObject space)
        {
            var spaceId = space["name"]?.GetValue<string>();
            if (spaceId is not null)
            {
                var displayName = space["displayName"]?.GetValue<string>() ?? "Direct message";
                await _spaces.UpsertAsync(spaceId, displayName, ct);
                await _spaces.SaveChangesAsync(ct);
            }
        }
        else if (type == "MESSAGE" && LinkCodeIn(body) is { } link)
        {
            // Linking a space to an organization (multi-tenancy Phase 2c): answered synchronously, in the space.
            var reply = await _mediator.Send(new LinkGoogleChatSpaceCommand(link.SpaceId, link.DisplayName, link.Code), ct);
            return Ok(new { text = reply.Data });
        }
        else if (type == "MESSAGE" && TryExtractMessage(body, out var spaceId, out var threadName, out var text))
        {
            // Unlike Slack's thread_ts or Teams' ReplyToId, every Chat message — including a brand
            // new one — already has a populated thread.name, so there's no cheap "is this a reply"
            // signal to filter on here. HandleGoogleChatReplyJob owns the real "is this a thread
            // Pulse started" decision via its own thread lookup; every genuine user message reaches
            // it and cheaply no-ops if it isn't one of ours.
            _jobs.Enqueue<HandleGoogleChatReplyJob>(j => j.ExecuteAsync(spaceId, threadName, text));
        }

        // Chat accepts an empty JSON object as "no synchronous reply" — the real answer, if any,
        // arrives later as its own message via the REST API from HandleGoogleChatReplyJob.
        return Ok(new { });
    }

    private static (string SpaceId, string DisplayName, string Code)? LinkCodeIn(JsonObject body)
    {
        if (body["message"] is not JsonObject message || body["space"] is not JsonObject space)
            return null;
        var argument = message["argumentText"]?.GetValue<string>() ?? message["text"]?.GetValue<string>();
        var match = argument is null ? null : LinkCommand.Match(argument);
        var spaceId = space["name"]?.GetValue<string>();
        if (match is not { Success: true } || spaceId is null)
            return null;
        return (spaceId, space["displayName"]?.GetValue<string>() ?? "Direct message", match.Groups[1].Value);
    }

    private static bool TryExtractMessage(JsonObject body, out string spaceId, out string threadName, out string text)
    {
        spaceId = string.Empty;
        threadName = string.Empty;
        text = string.Empty;

        if (body["message"] is not JsonObject message || body["space"] is not JsonObject space)
            return false;

        // Guarded defensively even though Chat's own sends go out via the REST API, not back
        // through this endpoint — the same belt-and-suspenders posture Slack's bot_id check has.
        if (message["sender"] is JsonObject sender && sender["type"]?.GetValue<string>() == "BOT")
            return false;

        var spaceIdValue = space["name"]?.GetValue<string>();
        var threadNameValue = (message["thread"] as JsonObject)?["name"]?.GetValue<string>();
        var textValue = message["text"]?.GetValue<string>();

        if (spaceIdValue is null || threadNameValue is null || string.IsNullOrWhiteSpace(textValue))
            return false;

        spaceId = spaceIdValue;
        threadName = threadNameValue;
        text = textValue;
        return true;
    }
}
