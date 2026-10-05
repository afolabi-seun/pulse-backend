using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Stamps the <c>service</c> identity onto every connection of the startup migration context, which has no HTTP request to
/// take an identity from. The row-level security policies treat the service role as unrestricted, so a data migration sees
/// every row of every organization.
/// </summary>
/// <remarks>
/// Without it a data-changing migration is silently filtered by FORCE ROW LEVEL SECURITY whenever its role does not bypass
/// RLS: the policy sees no <c>app.current_role</c>, evaluates false for every row, and an <c>UPDATE</c> "succeeds" having
/// touched nothing while still being recorded as applied.
///
/// This sets the session variables directly instead of going through <see cref="Pulse.Application.Common.Interfaces.IRlsContext"/>,
/// so it does not care what that interface returns: the request-scoped context grows an organization id as multi-tenancy lands,
/// and this identity is deliberately cross-organization (service role, no user, no organization). The organization variable is
/// cleared explicitly, so a pooled connection that last served a tenant's request cannot leak that tenant into a migration.
/// </remarks>
public sealed class ServiceIdentityConnectionInterceptor : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "select set_config('app.current_role', 'service', false), " +
            "set_config('app.current_user_id', '', false), " +
            "set_config('app.current_org_id', '', false)";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
