using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 1d: the organization boundary enforced by Postgres row-level security itself,
/// independent of the application. Queries run through a raw connection as a non-superuser probe role
/// (superusers bypass RLS), with the app.* session settings the RLS interceptor would stamp.
/// </summary>
[Collection("Integration")]
public class OrganizationRlsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private const string ProbeRole = "org_rls_probe";

    /// <summary>
    /// Tables deliberately without RLS. Anything else in public must have RLS enabled and forced —
    /// a new table that forgets it fails <see cref="Every_table_has_row_level_security_unless_explicitly_exempt"/>.
    /// </summary>
    private static readonly HashSet<string> ExemptFromRls =
    [
        "__EFMigrationsHistory",
        "project_organizations", // project → org lookup read by policies; ids only, no content
        // Their parents' RLS (tasks, sprints) encodes access rules beyond org, so chaining through it would
        // change who sees them within an org; covered by the EF org filters (Phase 1c) instead.
        "subtasks", "escalation_events", "automation_executions", "sprint_retrospectives",
    ];

    public OrganizationRlsTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Every_table_has_row_level_security_unless_explicitly_exempt()
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public' and c.relkind = 'r'
              and not (c.relrowsecurity and c.relforcerowsecurity)
            """;
        var unprotected = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                unprotected.Add(reader.GetString(0));

        unprotected.Should().BeSubsetOf(ExemptFromRls,
            "every table needs RLS enabled and forced, or a documented entry in ExemptFromRls");
    }

    [Fact]
    public async Task Org_B_sees_none_of_org_A_rows_at_the_database_level()
    {
        var world = await SeedAsync();
        var rows = new (string Table, string Where)[]
        {
            ("organizations", $"id = '{Organization.DefaultId}'"),
            ("teams", $"id = '{world.TeamId}'"),
            ("engineers", $"id = '{world.EngineerId}'"),
            ("projects", $"id = '{world.ProjectId}'"),
            ("project_members", $"project_id = '{world.ProjectId}' and engineer_id = '{world.OrgAUserId}'"),
            ("tasks", $"id = '{world.TaskId}'"),
            ("task_comments", $"id = '{world.CommentId}'"), // inherits through tasks
            ("sprints", $"id = '{world.SprintId}'"),
            ("notifications", $"id = '{world.NotificationId}'"),
        };

        foreach (var (table, where) in rows)
        {
            (await CountAsync(table, where, Roles.HeadOfPmo, world.OrgBUserId, world.OrgBId))
                .Should().Be(0, $"org B's head of PMO must not see org A's {table} row");
            (await CountAsync(table, where, Roles.HeadOfPmo, world.OrgAUserId, Organization.DefaultId))
                .Should().Be(1, $"control: org A's head of PMO sees its own {table} row");
            (await CountAsync(table, where, "service", null, null))
                .Should().Be(1, $"background work (no org) still sees {table}");
        }
    }

    [Fact]
    public async Task An_authenticated_caller_without_an_organization_sees_nothing()
    {
        var world = await SeedAsync();

        // HttpRlsContext stamps Guid.Empty for an authenticated caller whose token carries no org.
        (await CountAsync("projects", $"id = '{world.ProjectId}'", Roles.HeadOfPmo, world.OrgAUserId, Guid.Empty)).Should().Be(0);
        (await CountAsync("engineers", $"id = '{world.EngineerId}'", Roles.HeadOfPmo, world.OrgAUserId, Guid.Empty)).Should().Be(0);
    }

    [Fact]
    public async Task Anonymous_requests_can_still_find_an_engineer_for_login()
    {
        var world = await SeedAsync();

        (await CountAsync("engineers", $"id = '{world.EngineerId}'", role: "", userId: null, orgId: null)).Should().Be(1);
    }

    [Fact]
    public async Task An_assignee_who_is_not_a_project_member_still_sees_their_own_task()
    {
        // The reason project orgs come from project_organizations rather than projects: reading projects
        // inside the tasks policy would also apply projects' membership rule and hide this task.
        var world = await SeedAsync();

        (await CountAsync("tasks", $"id = '{world.TaskId}'", Roles.Engineer, world.EngineerId, Organization.DefaultId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Org_B_cannot_update_org_A_rows_or_move_a_row_into_org_A()
    {
        var world = await SeedAsync();

        (await ExecuteAsync($"update public.projects set name = name where id = '{world.ProjectId}'",
                Roles.HeadOfPmo, world.OrgBUserId, world.OrgBId))
            .Should().Be(0, "org A's project is invisible to org B's UPDATE");

        var moveIntoOrgA = () => ExecuteAsync(
            $"update public.teams set organization_id = '{Organization.DefaultId}' where id = '{world.OrgBTeamId}'",
            Roles.HeadOfPmo, world.OrgBUserId, world.OrgBId);
        await moveIntoOrgA.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.InsufficientPrivilege, "WITH CHECK rejects writing another org's id");
    }

    [Fact]
    public async Task New_projects_are_mirrored_into_project_organizations()
    {
        var project = await SeedProjectAsync("Mirror check");

        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select organization_id from public.project_organizations where project_id = '{project.Id}'";

        (await cmd.ExecuteScalarAsync()).Should().Be(project.OrganizationId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record World(
        Guid OrgBId, Guid OrgAUserId, Guid OrgBUserId, Guid OrgBTeamId, Guid TeamId, Guid EngineerId,
        Guid ProjectId, Guid TaskId, Guid CommentId, Guid SprintId, Guid NotificationId);

    private async Task<World> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid orgBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var orgB = Organization.Create("Org B", $"rls-b-{suffix}");
            db.Organizations.Add(orgB);
            await db.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        var orgAUser = await SeedEngineerAsync($"rls_a_{suffix}@pulse.io", Roles.HeadOfPmo);
        var orgBUser = await SeedEngineerAsync($"rls_b_{suffix}@pulse.io", Roles.HeadOfPmo);
        var orgBTeam = await SeedTeamAsync($"RLS B team {suffix}");
        await MoveToOrganizationAsync(orgBUser, orgBId);
        await MoveToOrganizationAsync(orgBTeam, orgBId);

        var team = await SeedTeamAsync($"RLS A team {suffix}");
        var engineer = await SeedEngineerAsync($"rls_eng_{suffix}@pulse.io");
        var project = await SeedProjectAsync($"RLS A project {suffix}", ownerTeamId: team.Id);
        await SeedProjectMemberAsync(project.Id, orgAUser.Id);
        var sprint = await SeedSprintAsync(team.Id, $"RLS A sprint {suffix}");
        var task = await SeedTaskAsync($"RLS A task {suffix}", project.Id, assigneeId: engineer.Id);
        var comment = await SeedCommentAsync(task.Id, engineer.Id);
        var notification = await SeedNotificationAsync(engineer.Id);

        return new World(orgBId, orgAUser.Id, orgBUser.Id, orgBTeam.Id, team.Id, engineer.Id,
            project.Id, task.Id, comment.Id, sprint.Id, notification.Id);
    }

    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    private Task<long> CountAsync(string table, string where, string role, Guid? userId, Guid? orgId) =>
        // Table names and conditions are fixed test literals, not input.
        RunAsProbeAsync($"select count(*) from public.{table} where {where}", role, userId, orgId,
            async cmd => (long)(await cmd.ExecuteScalarAsync())!);

    private Task<int> ExecuteAsync(string sql, string role, Guid? userId, Guid? orgId) =>
        RunAsProbeAsync(sql, role, userId, orgId, cmd => cmd.ExecuteNonQueryAsync());

    private async Task<T> RunAsProbeAsync<T>(string sql, string role, Guid? userId, Guid? orgId,
        Func<NpgsqlCommand, Task<T>> run)
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();

        await using (var setup = conn.CreateCommand())
        {
            // The runtime role's surface (docs/rls-runtime-role.sql): DML on public tables, app functions.
            setup.CommandText = $"""
                do $$ begin
                    if not exists (select 1 from pg_roles where rolname = '{ProbeRole}') then
                        create role {ProbeRole} nologin nosuperuser nobypassrls;
                    end if;
                end $$;
                grant usage on schema app to {ProbeRole};
                grant execute on all functions in schema app to {ProbeRole};
                grant select, insert, update, delete on all tables in schema public to {ProbeRole};
                select set_config('app.current_role', @role, false),
                       set_config('app.current_user_id', @uid, false),
                       set_config('app.current_org_id', @org, false);
                set role {ProbeRole};
                """;
            setup.Parameters.AddWithValue("role", role);
            setup.Parameters.AddWithValue("uid", userId?.ToString() ?? "");
            setup.Parameters.AddWithValue("org", orgId?.ToString() ?? "");
            await setup.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await run(cmd);
    }
}
