namespace Pulse.Application.Common.Interfaces;

/// <summary>Verifies that an inbound Google Chat request genuinely came from Google — the bearer
/// token in the request's Authorization header is a Google-issued JWT, verified against Google's
/// public certificates, with an issuer/audience check. Abstracted behind an interface (rather than
/// calling Google.Apis.Auth directly from the controller) so the controller's event-filtering logic
/// is testable without needing a real Google-signed token.</summary>
public interface IGoogleChatRequestVerifier
{
    Task<bool> VerifyAsync(string? bearerToken, CancellationToken ct = default);
}
