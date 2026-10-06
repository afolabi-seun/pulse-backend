using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pulse.Application.Integrations.Slack;

/// <summary>
/// The OAuth <c>state</c> for an "Add to Slack" install: which organization and engineer started it, when it
/// expires, and an HMAC over all of that. The callback is anonymous (Slack redirects the browser there), so
/// the state is the only proof of who is installing for which org — it must be unforgeable and short-lived.
/// </summary>
public static class SlackInstallState
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed record Payload(Guid Org, Guid Eng, long Exp, string Nonce);

    public static string Create(Guid organizationId, Guid engineerId, DateTimeOffset now, string signingKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(organizationId, engineerId,
            now.Add(Lifetime).ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(8))));
        return $"{Base64Url(payload)}.{Base64Url(Sign(payload, signingKey))}";
    }

    public static bool TryRead(string? state, DateTimeOffset now, string signingKey, out Guid organizationId, out Guid engineerId)
    {
        organizationId = engineerId = Guid.Empty;
        var parts = state?.Split('.');
        if (parts is not { Length: 2 })
            return false;

        try
        {
            var payloadBytes = FromBase64Url(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Sign(payloadBytes, signingKey), FromBase64Url(parts[1])))
                return false;
            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes);
            if (payload is null || DateTimeOffset.FromUnixTimeSeconds(payload.Exp) < now)
                return false;
            (organizationId, engineerId) = (payload.Org, payload.Eng);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    // A purpose-specific key derived from the app's signing key, so this HMAC can't be confused with a JWT's.
    private static byte[] Sign(byte[] payload, string signingKey) =>
        HMACSHA256.HashData(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), "slack-install-state"u8), payload);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
