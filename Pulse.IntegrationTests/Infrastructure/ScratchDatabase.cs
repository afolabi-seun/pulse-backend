using Pulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Pulse.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway database in the shared Testcontainers Postgres, for tests that need to stop the schema
/// at a specific migration — e.g. insert rows the way they existed before a migration, then migrate
/// forward and check what happened to them. Dropped on dispose.
/// </summary>
public sealed class ScratchDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _name;

    public string ConnectionString { get; }

    private ScratchDatabase(string adminConnectionString, string name)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }

    public static async Task<ScratchDatabase> CreateAsync(string adminConnectionString)
    {
        var db = new ScratchDatabase(adminConnectionString, $"scratch_{Guid.NewGuid():N}");
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE {db._name}");
        return db;
    }

    public async Task MigrateToAsync(string migration)
    {
        var options = new DbContextOptionsBuilder<PulseDbContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new PulseDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    /// <summary>
    /// Inserts rows filling every NOT NULL column the table has at its current schema with a
    /// type-appropriate placeholder — so tests don't hard-code (and silently drift from) the column
    /// list as tables evolve. Columns with defaults are filled too, since some defaults (e.g.
    /// projects.code = '') would collide on unique indexes. Strings are random for the same reason.
    /// </summary>
    public async Task InsertRowsAsync(string table, int count)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var columns = new List<(string Name, string Type, int? MaxLength)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT column_name, data_type, character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table AND is_nullable = 'NO'
                """;
            cmd.Parameters.AddWithValue("table", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        var values = columns.Select(c => c.Type switch
        {
            "uuid" => "gen_random_uuid()",
            "character varying" or "text" =>
                $"upper(substr(translate(md5(random()::text), '0123456789', 'GHIJKLMNOP'), 1, {Math.Min(c.MaxLength ?? 10, 10)}))",
            "integer" or "bigint" or "smallint" or "numeric" or "double precision" or "real" => "0",
            "boolean" => "false",
            "timestamp with time zone" or "timestamp without time zone" or "date" => "now()",
            "jsonb" or "json" => "'{}'",
            _ => throw new NotSupportedException($"{table}.{c.Name} has unhandled type {c.Type}; extend InsertRowsAsync."),
        });

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => $"\"{c.Name}\""))}) VALUES ({string.Join(", ", values)})";
        for (var i = 0; i < count; i++)
            await ExecuteAsync(ConnectionString, sql);
    }

    public Task<long> ScalarAsync(string sql) => ScalarAsync(ConnectionString, sql);

    public static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
    }
}
