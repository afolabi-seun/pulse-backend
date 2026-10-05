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

        if (RlsServiceOverride.IsSet(ctx))
            return ("service", "", ""); // narrow, explicit opt-in — see RlsServiceOverride

        if (_currentUser.IsAuthenticated)
            return (_currentUser.Role ?? "engineer",
                    _currentUser.UserId?.ToString() ?? "",
                    _currentUser.OrganizationId?.ToString() ?? "");

        return ("", "", ""); // unauthenticated request — only RLS-exempt tables (e.g. auth) are reachable
    }
}
