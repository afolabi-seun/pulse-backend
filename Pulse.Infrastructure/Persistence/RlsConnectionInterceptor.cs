using System.Data.Common;
using Pulse.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Stamps the current identity onto every database connection as session-level settings
/// (<c>app.current_role</c> / <c>app.current_user_id</c>) so Postgres row-level security policies
/// can see who is acting. Runs on every connection open — including pool check-outs — so a value
/// left by a previous caller is always overwritten before any query runs.
/// </summary>
/// <remarks>
/// Uses <c>set_config(..., is_local := false)</c> (session scope), not the transaction-scoped form,
/// so the setting survives across the multiple statements EF issues on one open connection.
/// </remarks>
public class RlsConnectionInterceptor : DbConnectionInterceptor
{
    private readonly IRlsContext _rls;

    public RlsConnectionInterceptor(IRlsContext rls) => _rls = rls;

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

    private async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        var (role, userId) = _rls.Resolve();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "select set_config('app.current_role', @role, false), set_config('app.current_user_id', @uid, false)";

        var roleParam = cmd.CreateParameter();
        roleParam.ParameterName = "role";
        roleParam.Value = role;
        cmd.Parameters.Add(roleParam);

        var uidParam = cmd.CreateParameter();
        uidParam.ParameterName = "uid";
        uidParam.Value = userId;
        cmd.Parameters.Add(uidParam);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
