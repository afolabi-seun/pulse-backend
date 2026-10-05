using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Always identifies as the <c>service</c> role, which row-level security treats as unrestricted. Used by the
/// startup migration connection, which has no HTTP request to take an identity from.
/// </summary>
/// <remarks>
/// Without this a data-changing migration is silently filtered by FORCE ROW LEVEL SECURITY whenever its role
/// does not bypass RLS: the policy sees no <c>app.current_role</c>, evaluates false for every row, and an
/// <c>UPDATE</c> "succeeds" having touched nothing (the FixQaSubtaskType incident). Stamping the service role
/// makes the migration see the same rows a background job does, so it no longer depends on the owner role
/// having BYPASSRLS.
/// </remarks>
public sealed class ServiceRlsContext : IRlsContext
{
    public (string Role, string UserId) Resolve() => ("service", string.Empty);
}
