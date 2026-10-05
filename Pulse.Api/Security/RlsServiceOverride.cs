using Microsoft.AspNetCore.Http;

namespace Pulse.Api.Security;

/// <summary>
/// Marks the current HTTP request to be treated as the <c>service</c> role for row-level
/// security, even though it carries no authenticated user. Reserved for narrow,
/// environment-gated internal tooling — e.g. dev-only demo seeding — that legitimately needs
/// unrestricted writes despite being reachable without a login. Never apply this to a
/// user-facing endpoint: it bypasses the "unauthenticated = no tenant access" default that
/// every other anonymous route relies on.
/// </summary>
public static class RlsServiceOverride
{
    private const string ItemsKey = "Pulse.RlsServiceOverride";

    public static void Apply(HttpContext ctx) => ctx.Items[ItemsKey] = true;

    public static bool IsSet(HttpContext ctx) => ctx.Items.ContainsKey(ItemsKey);
}
