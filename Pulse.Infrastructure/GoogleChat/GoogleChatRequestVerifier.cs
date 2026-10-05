using Pulse.Application.Common.Interfaces;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.GoogleChat;

/// <summary>Verifies an inbound Google Chat request's bearer token against Google's own public
/// certificates via Google.Apis.Auth — the same reasoning that justified using the Bot Framework
/// SDK for Teams' inbound validation rather than hand-rolling it: this is a rotating-key,
/// third-party-issued JWT, not a shared-secret HMAC like Slack's, so it isn't worth re-implementing.</summary>
public class GoogleChatRequestVerifier : IGoogleChatRequestVerifier
{
    // Every Google Chat app request is signed by this fixed service account — confirmed as part of
    // verification so a token that's merely well-formed and Google-signed (but for something else
    // entirely) doesn't pass.
    private const string ChatSystemServiceAccountEmail = "chat@system.gserviceaccount.com";

    private readonly IAppSettings _settings;
    private readonly ILogger<GoogleChatRequestVerifier> _logger;

    public GoogleChatRequestVerifier(IAppSettings settings, ILogger<GoogleChatRequestVerifier> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken) || string.IsNullOrEmpty(_settings.GoogleChatAudience))
            return false;

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(bearerToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _settings.GoogleChatAudience },
            });

            return payload.Email == ChatSystemServiceAccountEmail && payload.EmailVerified;
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogWarning(ex, "Google Chat request token failed verification");
            return false;
        }
    }
}
