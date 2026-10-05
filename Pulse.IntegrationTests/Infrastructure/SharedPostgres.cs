using Pulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Pulse.IntegrationTests.Infrastructure;

/// <summary>
/// One Postgres container for the whole test run, instead of one per test class.
///
/// The suite has dozens of test classes, and each used to start its own container and apply every migration from scratch, which was most of
/// a 20-plus minute run. Now the container starts once, the migrations run once into a template database, and each class gets its own
/// database cloned from that template (<c>CREATE DATABASE ... TEMPLATE</c>, a file copy, so effectively instant). Classes keep the isolation
/// they had: nobody sees another class's rows, roles that a test creates are cluster-wide but guarded by the tests' own existence checks.
/// </summary>
internal static class SharedPostgres
{
    private const string TemplateDatabase = "pulse_template";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static string? _adminConnectionString;

    public static async Task<string> CreateDatabaseAsync()
    {
        var admin = await EnsureStartedAsync();
        var name = $"test_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""CREATE DATABASE "{name}" TEMPLATE "{TemplateDatabase}" """;
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var admin = _adminConnectionString;
        if (admin is null) return;

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // FORCE disconnects whatever the class's host still has open, so a lingering pooled connection cannot block the drop.
        command.CommandText = $"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> EnsureStartedAsync()
    {
        if (_adminConnectionString is not null) return _adminConnectionString;

        await Gate.WaitAsync();
        try
        {
            if (_adminConnectionString is not null) return _adminConnectionString;

            var container = new PostgreSqlBuilder()
                .WithDatabase("pulse_test")
                .WithUsername("pulse")
                .WithPassword("testpassword")
                .Build();
            await container.StartAsync();

            var admin = container.GetConnectionString();
            await CreateMigratedTemplateAsync(admin);

            _container = container;
            _adminConnectionString = admin;
            return admin;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task CreateMigratedTemplateAsync(string admin)
    {
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"""CREATE DATABASE "{TemplateDatabase}" """;
            await create.ExecuteNonQueryAsync();
        }

        var templateConnection = new NpgsqlConnectionStringBuilder(admin) { Database = TemplateDatabase }.ConnectionString;
        var options = new DbContextOptionsBuilder<PulseDbContext>().UseNpgsql(templateConnection).Options;
        await using (var db = new PulseDbContext(options))
            await db.Database.MigrateAsync();

        // A template cannot be cloned while anything is connected to it.
        NpgsqlConnection.ClearAllPools();
    }
}
