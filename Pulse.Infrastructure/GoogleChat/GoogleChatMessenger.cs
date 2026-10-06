using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pulse.Application.Common.Interfaces;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.GoogleChat;

/// <summary>Posts into a Google Chat space the app has already been added to, via the Chat REST
/// API's spaces.messages.create, authenticated as a service account (GoogleCredential handles the
/// service-account JWT-bearer OAuth2 exchange and its own token caching/refresh — no need to
/// hand-roll either). Mirrors SlackClient/TeamsMessenger's shape and never-throws contract; every
/// failure (no credentials, malformed key, the send itself failing) returns null rather than
/// propagating, so a drafting/delivery failure here can never block an alert.</summary>
public class GoogleChatMessenger : IGoogleChatMessenger
{
    private const string Scope = "https://www.googleapis.com/auth/chat.bot";

    private readonly HttpClient _http;
    private readonly IAppSettings _settings;
    private readonly IGoogleChatSpaceRepository _spaces;
    private readonly ILogger<GoogleChatMessenger> _logger;
    private GoogleCredential? _credential;

    public GoogleChatMessenger(HttpClient http, IAppSettings settings, IGoogleChatSpaceRepository spaces,
        ILogger<GoogleChatMessenger> logger)
    {
        _http = http;
        _settings = settings;
        _spaces = spaces;
        _logger = logger;
    }

    public async Task<string?> PostToSpaceAsync(string spaceId, string text, string? threadName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.GoogleChatServiceAccountJson))
            return null;

        // Every organization shares the one Chat app, so the app itself could post anywhere it's been added.
        // Only post to a space linked to the acting organization (the lookup is org-filtered): never to
        // another organization's space, and never to an unlinked one (multi-tenancy Phase 2c).
        if ((await _spaces.GetBySpaceIdAsync(spaceId, ct))?.OrganizationId is null)
        {
            _logger.LogWarning("Not posting to Google Chat space {SpaceId}: it isn't linked to this organization.", spaceId);
            return null;
        }

        return await SendAsync(spaceId, text, threadName, ct);
    }

    /// <summary>A person's direct-message space with Pulse, captured from their own ADDED_TO_SPACE event and
    /// stored against their account (PersonalChatSettings) — so it's already bound to that one person and
    /// isn't one of the shared spaces the organization check above is for.</summary>
    public async Task<string?> PostToDirectMessageAsync(string dmSpace, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.GoogleChatServiceAccountJson))
            return null;
        return await SendAsync(dmSpace, text, null, ct);
    }

    private async Task<string?> SendAsync(string spaceId, string text, string? threadName, CancellationToken ct)
    {
        try
        {
            _credential ??= GoogleCredential.FromJson(_settings.GoogleChatServiceAccountJson).CreateScoped(Scope);
            // GoogleCredential implements ITokenAccess explicitly, so the interface member isn't
            // directly visible on the class reference.
            var accessToken = await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(cancellationToken: ct);

            var payload = new Dictionary<string, object?> { ["text"] = text };
            var url = $"https://chat.googleapis.com/v1/{spaceId}/messages";
            if (threadName is not null)
            {
                payload["thread"] = new { name = threadName };
                url += "?messageReplyOption=REPLY_MESSAGE_OR_FAIL";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(payload);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Chat spaces.messages.create HTTP failure: {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            // Every Chat message belongs to a thread, even a brand-new top-level one — the API
            // always returns this, which is what lets a later reply thread against it.
            return json?["thread"]?["name"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Chat message post to space {SpaceId} failed", spaceId);
            return null;
        }
    }
}
