using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Slack;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>Receives Slack's Events API callbacks so a reply inside an alert's Slack thread can be
/// answered by IAlertExplainer. Called by Slack's own servers, not a Pulse user — carries no
/// [EngineerAuth] (EngineerAuthAttribute is opt-in per controller, not a global filter), and is
/// authenticated instead by verifying Slack's own request signature. Never does any real work
/// synchronously: Slack expects a response within ~3 seconds, so a matching event is handed off to
/// HandleSlackReplyJob and this returns immediately.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations/slack/events")]
[Tags("Slack")]
public class SlackEventsController : ControllerBase
{
    private static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);

    private readonly IAppSettings _settings;
    private readonly IBackgroundJobClient _jobs;

    public SlackEventsController(IAppSettings settings, IBackgroundJobClient jobs)
    {
        _settings = settings;
        _jobs = jobs;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }
        Request.Body.Position = 0;

        if (!IsValidSignature(rawBody))
            return Unauthorized();

        var body = JsonNode.Parse(rawBody)?.AsObject();
        if (body is null)
            return BadRequest();

        // Slack's one-time handshake when the Events API subscription URL is first configured —
        // must be answered with the challenge value, verbatim, as plain text.
        if (body["type"]?.GetValue<string>() == "url_verification")
            return Content(body["challenge"]?.GetValue<string>() ?? "", "text/plain");

        if (body["type"]?.GetValue<string>() == "event_callback" && body["event"]?.AsObject() is { } evt && ShouldHandle(evt))
        {
            var channel = evt["channel"]!.GetValue<string>();
            var threadTs = evt["thread_ts"]!.GetValue<string>();
            var text = evt["text"]?.GetValue<string>() ?? "";
            // Which workspace — and so which organization — the reply came from (multi-tenancy Phase 2b).
            var teamId = body["team_id"]?.GetValue<string>();
            _jobs.Enqueue<HandleSlackReplyJob>(j => j.ExecuteAsync(teamId, channel, threadTs, text));
        }

        // Always 200 — Slack retries aggressively on anything else, including events we intentionally ignore.
        return Ok();
    }

    /// <summary>Only a genuine threaded human reply: no bot_id (Pulse's own posts would otherwise
    /// echo back into an infinite loop), no subtype (ignores edits/deletes/joins), and a thread_ts
    /// (ignores new top-level channel messages — this only answers replies to an alert Pulse already posted).</summary>
    private static bool ShouldHandle(JsonObject evt) =>
        evt["type"]?.GetValue<string>() == "message"
        && evt["bot_id"] is null
        && evt["subtype"] is null
        && evt["thread_ts"] is not null
        && evt["channel"] is not null;

    private bool IsValidSignature(string rawBody)
    {
        if (string.IsNullOrEmpty(_settings.SlackSigningSecret))
            return false;

        if (!Request.Headers.TryGetValue("X-Slack-Signature", out var signatureHeader) ||
            !Request.Headers.TryGetValue("X-Slack-Request-Timestamp", out var timestampHeader))
            return false;

        if (!long.TryParse(timestampHeader, out var timestamp))
            return false;

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if ((DateTimeOffset.UtcNow - requestTime).Duration() > MaxRequestAge)
            return false; // stale — reject to guard against a replayed request

        var baseString = $"v0:{timestamp}:{rawBody}";
        var key = Encoding.UTF8.GetBytes(_settings.SlackSigningSecret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(baseString));
        var computedSignature = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();

        var expected = Encoding.UTF8.GetBytes(computedSignature);
        var actual = Encoding.UTF8.GetBytes(signatureHeader.ToString());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
