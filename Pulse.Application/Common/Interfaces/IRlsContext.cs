namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// Supplies the identity stamped onto each database connection for row-level security.
/// Resolved per connection-open by <c>RlsConnectionInterceptor</c>.
/// </summary>
public interface IRlsContext
{
    /// <summary>
    /// Returns the role, user id and organization id to apply as Postgres session settings
    /// (<c>app.current_role</c> / <c>app.current_user_id</c> / <c>app.current_org_id</c>).
    /// <list type="bullet">
    /// <item>Authenticated HTTP request → the caller's real role, id and organization (organization is
    /// empty for a token issued before the org_id claim existed, until it's refreshed).</item>
    /// <item>Background work (no HTTP context) → the <c>service</c> role, which RLS treats as unrestricted,
    /// and no organization.</item>
    /// <item>Unauthenticated HTTP request → empty role/id/organization (no tenant access; only RLS-exempt
    /// tables are reachable).</item>
    /// </list>
    /// </summary>
    (string Role, string UserId, string OrganizationId) Resolve();
}
