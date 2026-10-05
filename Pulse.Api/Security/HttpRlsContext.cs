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
    private readonly ICurrentUserService _currentUser;

    public HttpRlsContext(IHttpContextAccessor http, ICurrentUserService currentUser)
    {
        _http = http;
        _currentUser = currentUser;
    }

    public (string Role, string UserId, string OrganizationId) Resolve()
    {
        var ctx = _http.HttpContext;
        if (ctx is null)
            return ("service", "", ""); // background work — no request in flight

        // An authenticated caller is always bounded to an organization at the database: their own, or
        // Guid.Empty — which matches no row — if the token carries none. An empty org means "no org
        // boundary" to app.org_visible(), which is only right for background work and anonymous requests.
        var orgId = _currentUser.IsAuthenticated
            ? (_currentUser.OrganizationId ?? Guid.Empty).ToString()
            : "";

        if (RlsServiceOverride.IsSet(ctx))
            return ("service", "", orgId); // narrow, explicit opt-in — see RlsServiceOverride; still org-bounded

        if (_currentUser.IsAuthenticated)
            return (_currentUser.Role ?? "engineer", _currentUser.UserId?.ToString() ?? "", orgId);

        return ("", "", ""); // unauthenticated request — only RLS-exempt tables (e.g. auth) are reachable
    }
}
