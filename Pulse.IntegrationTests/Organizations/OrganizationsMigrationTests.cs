using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 0: the organizations table exists with the default org, and every team,
/// engineer and project — pre-existing or newly inserted — belongs to it.
/// </summary>
[Collection("Integration")]
public class OrganizationsMigrationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private const string MigrationBefore = "20261004024120_AddEstimationApprovalEscalation";
    private const string MigrationUnderTest = "20261005175241_AddOrganizations";
    private static readonly string[] RootTables = ["teams", "engineers", "projects"];

    public OrganizationsMigrationTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task The_default_organization_is_seeded()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        var org = await db.Organizations.SingleAsync(o => o.Id == Organization.DefaultId);

        org.Slug.Should().Be(Organization.DefaultSlug);
        org.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task New_teams_engineers_and_projects_land_in_the_default_organization()
    {
        var team = await SeedTeamAsync("Org Phase0 Team");
        var engineer = await SeedEngineerAsync("org_phase0@pulse.io");
        var project = await SeedProjectAsync("Org Phase0 Project");

        // EF reads the database-generated default back on insert.
        team.OrganizationId.Should().Be(Organization.DefaultId);
        engineer.OrganizationId.Should().Be(Organization.DefaultId);
        project.OrganizationId.Should().Be(Organization.DefaultId);
    }

    [Fact]
    public async Task Rows_that_existed_before_the_migration_are_assigned_to_the_default_organization()
    {
        // A throwaway database in the same container, so rows can be inserted at the pre-migration schema.
        var dbName = $"org_migration_{Guid.NewGuid():N}";
        await ExecuteAsync(Factory.ConnectionString, $"CREATE DATABASE {dbName}");
        var connStr = new NpgsqlConnectionStringBuilder(Factory.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateToAsync(connStr, MigrationBefore);
            foreach (var table in RootTables)
                await InsertRowsAsync(connStr, table, count: 3);

            await MigrateToAsync(connStr, MigrationUnderTest);

            foreach (var table in RootTables)
            {
                (await ScalarAsync(connStr, $"SELECT count(*) FROM {table}")).Should().Be(3, table);
                (await ScalarAsync(connStr, $"SELECT count(*) FROM {table} WHERE organization_id = '{Organization.DefaultId}'"))
                    .Should().Be(3, $"every pre-existing {table} row should belong to the default org");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(Factory.ConnectionString, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task The_migration_rolls_back_cleanly()
    {
        var dbName = $"org_rollback_{Guid.NewGuid():N}";
        await ExecuteAsync(Factory.ConnectionString, $"CREATE DATABASE {dbName}");
        var connStr = new NpgsqlConnectionStringBuilder(Factory.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await MigrateToAsync(connStr, MigrationUnderTest);
            await MigrateToAsync(connStr, MigrationBefore);

            (await ScalarAsync(connStr, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'organizations'"))
                .Should().Be(0);
            (await ScalarAsync(connStr, "SELECT count(*) FROM information_schema.columns WHERE column_name = 'organization_id'"))
                .Should().Be(0);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(Factory.ConnectionString, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    private static async Task MigrateToAsync(string connStr, string migration)
    {
        var options = new DbContextOptionsBuilder<PulseDbContext>().UseNpgsql(connStr).Options;
        await using var db = new PulseDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    /// <summary>
    /// Inserts rows filling every NOT NULL column the table has at its current schema with a
    /// type-appropriate placeholder — so the test doesn't hard-code (and silently drift from) the
    /// column list as these tables evolve. Columns with defaults are filled too, since some defaults
    /// (e.g. projects.code = '') would collide on unique indexes. Strings are random for the same reason.
    /// </summary>
    private static async Task InsertRowsAsync(string connStr, string table, int count)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var columns = new List<(string Name, string Type, int? MaxLength)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT column_name, data_type, character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table
                  AND is_nullable = 'NO'
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

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", values)})";
        for (var i = 0; i < count; i++)
            await ExecuteAsync(conn, sql);
    }

    private static async Task ExecuteAsync(string connStr, string sql)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await ExecuteAsync(conn, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connStr, string sql)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
