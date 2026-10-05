namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// Supplies the identity stamped onto each database connection for row-level security.
/// Resolved per connection-open by <c>RlsConnectionInterceptor</c>.
/// </summary>
public interface IRlsContext
{
    /// <summary>
    /// Returns the role and user id to apply as Postgres session settings
    /// (<c>app.current_role</c> / <c>app.current_user_id</c>).
    /// <list type="bullet">
    /// <item>Authenticated HTTP request → the caller's real role and id.</item>
    /// <item>Background work (no HTTP context) → the <c>service</c> role, which RLS treats as unrestricted.</item>
    /// <item>Unauthenticated HTTP request → empty role/id (no tenant access; only RLS-exempt tables are reachable).</item>
    /// </list>
    /// </summary>
    (string Role, string UserId) Resolve();
}
