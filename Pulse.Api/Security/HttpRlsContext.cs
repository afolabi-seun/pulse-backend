using System.Security.Claims;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Api.Security;

/// <summary>
/// Resolves the RLS identity from the ambient HTTP request. When there is no HTTP context
/// (Hangfire jobs, the startup migration runner) the caller is treated as the <c>service</c>
/// role, which RLS policies exempt from tenant filtering.
/// </summary>
public class HttpRlsContext : IRlsContext
{
    private readonly IHttpContextAccessor _http;

    public HttpRlsContext(IHttpContextAccessor http) => _http = http;

    public (string Role, string UserId) Resolve()
    {
        var ctx = _http.HttpContext;
        if (ctx is null)
            return ("service", ""); // background work — no request in flight

        if (RlsServiceOverride.IsSet(ctx))
            return ("service", ""); // narrow, explicit opt-in — see RlsServiceOverride

        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true)
            return (user.FindFirstValue(ClaimTypes.Role) ?? "engineer",
                    user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");

        return ("", ""); // unauthenticated request — only RLS-exempt tables (e.g. auth) are reachable
    }
}
