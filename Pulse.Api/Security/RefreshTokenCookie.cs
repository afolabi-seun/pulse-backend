namespace Pulse.Api.Security;

/// <summary>
/// The refresh token's home in the browser: an httpOnly cookie that scripts on the page cannot read, scoped to the
/// auth endpoints and sent only for same-site requests. It replaces keeping the 14-day token in localStorage,
/// where any script-injection bug could copy it.
/// </summary>
/// <remarks>
/// <c>SameSite=Strict</c> means a cross-site page cannot make the browser attach it, which is the main defence
/// against forged refresh or logout requests. Because the frontend and API are different origins, a request that
/// relies on the cookie must also carry <see cref="ClientHeader"/>: a custom header forces a CORS preflight, which
/// only the configured frontend origins pass, so it works as a second, independent check.
/// </remarks>
public static class RefreshTokenCookie
{
    public const string Name = "pulse_refresh";
    public const string ClientHeader = "X-Pulse-Client";

    // The refresh token is valid for 14 days absolute (see LoginCommand); the server enforces the real expiry.
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    private const string CookiePath = "/api/v1/auth";

    public static void Write(HttpResponse response, string token, bool secure) =>
        response.Cookies.Append(Name, token, Options(secure, DateTimeOffset.UtcNow.Add(Lifetime)));

    public static void Clear(HttpResponse response, bool secure) =>
        response.Cookies.Delete(Name, Options(secure, null));

    public static string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public static bool HasClientHeader(HttpRequest request) => request.Headers.ContainsKey(ClientHeader);

    private static CookieOptions Options(bool secure, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
