using System.Net;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Infrastructure.Persistence;

namespace Pulse.IntegrationTests.Security;

/// <summary>
/// Verifies row-level security end-to-end. The app layer (ProjectAccessPolicy) is the primary gate;
/// these tests also reach past it with a raw connection to prove the database policies themselves deny
/// cross-tenant access — the defense-in-depth guarantee.
/// </summary>
[Collection("Integration")]
public class RlsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public RlsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── App layer (full pipeline, interceptor + policies active) ──────────────

    [Fact]
    public async Task Engineer_can_read_a_task_in_their_project_but_not_one_in_another()
    {
        var engineer = await SeedEngineerAsync("rls_eng@pulse.io", Roles.Engineer);
        var projectA = await SeedProjectAsync("RLS Project A");
        var projectB = await SeedProjectAsync("RLS Project B");
        await SeedProjectMemberAsync(projectA.Id, engineer.Id);

        var ownTask = await SeedTaskAsync("Mine", projectA.Id, assigneeId: engineer.Id);
        var otherTask = await SeedTaskAsync("Theirs", projectB.Id);

        var client = await AuthenticatedClientAsync("rls_eng@pulse.io");

        (await client.GetAsync($"/api/v1/tasks/{ownTask.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"/api/v1/tasks/{otherTask.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── DB layer (raw connection, bypassing the application entirely) ──────────

    [Fact]
    public async Task Policy_filters_tasks_to_member_projects_when_acting_as_an_engineer()
    {
        var engineer = await SeedEngineerAsync("rls_db_eng@pulse.io", Roles.Engineer);
        var projectA = await SeedProjectAsync("RLS DB Project A");
        var projectB = await SeedProjectAsync("RLS DB Project B");
        await SeedProjectMemberAsync(projectA.Id, engineer.Id);

        // Note: neither task is assigned — visibility must come purely from project membership.
        var inMemberProject = await SeedTaskAsync("Visible", projectA.Id);
        var inOtherProject = await SeedTaskAsync("Hidden", projectB.Id);

        (await CountAsAsync("tasks", inMemberProject.Id, Roles.Engineer, engineer.Id)).Should().Be(1);
        (await CountAsAsync("tasks", inOtherProject.Id, Roles.Engineer, engineer.Id)).Should().Be(0);
        // A background/service identity sees everything (RLS treats 'service' as global).
        (await CountAsAsync("tasks", inOtherProject.Id, "service", null)).Should().Be(1);
    }

    [Fact]
    public async Task Child_rows_inherit_their_parent_tasks_visibility()
    {
        var engineer = await SeedEngineerAsync("rls_comment_eng@pulse.io", Roles.Engineer);
        var author = await SeedEngineerAsync("rls_comment_author@pulse.io", Roles.Engineer);
        var projectA = await SeedProjectAsync("RLS Comment Project A");
        var projectB = await SeedProjectAsync("RLS Comment Project B");
        await SeedProjectMemberAsync(projectA.Id, engineer.Id);

        var taskInA = await SeedTaskAsync("A task", projectA.Id);
        var taskInB = await SeedTaskAsync("B task", projectB.Id);
        var visibleComment = await SeedCommentAsync(taskInA.Id, author.Id);
        var hiddenComment = await SeedCommentAsync(taskInB.Id, author.Id);

        (await CountAsAsync("task_comments", visibleComment.Id, Roles.Engineer, engineer.Id)).Should().Be(1);
        (await CountAsAsync("task_comments", hiddenComment.Id, Roles.Engineer, engineer.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Engineer_can_view_another_teams_sprint_when_a_member_project_has_a_task_in_it()
    {
        var engineer = await SeedEngineerAsync("rls_xsprint_eng@pulse.io", Roles.Engineer);
        var otherTeam = await SeedTeamAsync("RLS Cross Team");
        var myProject = await SeedProjectAsync("RLS Cross Project");
        await SeedProjectMemberAsync(myProject.Id, engineer.Id);

        // A sprint run by a team the engineer is NOT on, but containing a task in their project.
        var sprint = await SeedSprintAsync(otherTeam.Id, "Cross-team Sprint");
        var task = await SeedTaskAsync("In cross sprint", myProject.Id);
        await AssignTaskToSprintAsync(task.Id, sprint.Id);

        var client = await AuthenticatedClientAsync("rls_xsprint_eng@pulse.io");

        (await client.GetAsync($"/api/v1/sprints/{sprint.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sprints_are_visible_only_to_their_own_team()
    {
        var engineer = await SeedEngineerAsync("rls_sprint_eng@pulse.io", Roles.Engineer);
        var myTeam = await SeedTeamAsync("RLS My Team");
        var otherTeam = await SeedTeamAsync("RLS Other Team");
        await AssignEngineerToTeamAsync(engineer.Id, myTeam.Id);

        var mySprint = await SeedSprintAsync(myTeam.Id, "My Sprint");
        var otherSprint = await SeedSprintAsync(otherTeam.Id, "Other Sprint");

        (await CountAsAsync("sprints", mySprint.Id, Roles.Engineer, engineer.Id)).Should().Be(1);
        (await CountAsAsync("sprints", otherSprint.Id, Roles.Engineer, engineer.Id)).Should().Be(0);
        (await CountAsAsync("sprints", otherSprint.Id, "service", null)).Should().Be(1);
    }

    // ── Org-wide read-only viewers (Executive / HR / Accountant) ───────────────

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Org_read_only_roles_can_read_rows_in_projects_they_are_not_members_of(string role)
    {
        // Regression: these roles were missing from app.is_global_role(), so under the restricted runtime
        // role every task/project/sprint row was stripped — Engineers and Reports showed 0 points.
        var viewer = await SeedEngineerAsync($"rls_viewer_{role}@pulse.io", role);
        var author = await SeedEngineerAsync($"rls_viewer_author_{role}@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync($"RLS Viewer Team {role}");
        var project = await SeedProjectAsync($"RLS Viewer Project {role}", team.Id);
        var task = await SeedTaskAsync("Not visible by membership", project.Id, assigneeId: author.Id);
        var comment = await SeedCommentAsync(task.Id, author.Id);
        var sprint = await SeedSprintAsync(team.Id, "Viewer Sprint");

        (await CountAsAsync("projects", project.Id, role, viewer.Id)).Should().Be(1);
        (await CountAsAsync("tasks", task.Id, role, viewer.Id)).Should().Be(1);
        (await CountAsAsync("sprints", sprint.Id, role, viewer.Id)).Should().Be(1);
        (await CountAsAsync("task_comments", comment.Id, role, viewer.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Org_read_only_roles_cannot_modify_tasks_at_the_database_level(string role)
    {
        var viewer = await SeedEngineerAsync($"rls_viewer_write_{role}@pulse.io", role);
        var project = await SeedProjectAsync($"RLS Viewer Write Project {role}");
        var task = await SeedTaskAsync("Read-only target", project.Id);

        (await UpdateTaskTitleAsAsync(task.Id, role, viewer.Id)).Should().Be(0);
        // The same statement as a global role proves the probe grants themselves are not what blocks it.
        (await UpdateTaskTitleAsAsync(task.Id, Roles.HeadOfPmo, viewer.Id)).Should().Be(1);
    }

    // ── Personal projects (HR/Accountant private to-dos) ───────────────────────

    private async Task<(Guid ProjectId, Guid TaskId)> SeedPersonalProjectWithTaskAsync(Engineer owner)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var project = Pulse.Domain.Projects.Project.CreatePersonal(owner.Id, owner.Name, $"P{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}");
        db.Projects.Add(project);
        db.ProjectMembers.Add(Pulse.Domain.Projects.ProjectMember.Create(project.Id, owner.Id));
        var task = Pulse.Domain.Tasks.PulseTask.Create("Personal to-do", 0, project.Id, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), createdById: owner.Id);
        task.AssignTaskNumber(1);
        task.Assign(owner.Id, owner.Id);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return (project.Id, task.Id);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Another_org_read_only_role_cannot_see_a_personal_project_or_its_tasks(string viewerRole)
    {
        var owner = await SeedEngineerAsync($"rls_personal_owner_{viewerRole}@pulse.io", Roles.HR);
        var viewer = await SeedEngineerAsync($"rls_personal_viewer_{viewerRole}@pulse.io", viewerRole);
        var (projectId, taskId) = await SeedPersonalProjectWithTaskAsync(owner);

        (await CountAsAsync("projects", projectId, viewerRole, viewer.Id)).Should().Be(0);
        (await CountAsAsync("tasks", taskId, viewerRole, viewer.Id)).Should().Be(0,
            "the org-wide read policy must not reach into someone else's personal project");

        // The owner still sees their own, and a global role (PMO) can still reach it.
        (await CountAsAsync("projects", projectId, Roles.HR, owner.Id)).Should().Be(1);
        (await CountAsAsync("tasks", taskId, Roles.HR, owner.Id)).Should().Be(1);
        (await CountAsAsync("projects", projectId, Roles.HeadOfPmo, viewer.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_person_can_create_their_own_personal_project_row_but_not_another_persons()
    {
        var owner = await SeedEngineerAsync("rls_personal_insert@pulse.io", Roles.HR);
        var other = await SeedEngineerAsync("rls_personal_insert_other@pulse.io", Roles.HR);

        (await TryInsertPersonalProjectAsAsync(owner.Id, Roles.HR, owner.Id)).Should().BeTrue(
            "the owner is not a member yet, so a member-only policy would reject the very insert that creates it");
        (await TryInsertPersonalProjectAsAsync(other.Id, Roles.HR, owner.Id)).Should().BeFalse(
            "a personal project can only be created for yourself");
    }

    private async Task<bool> TryInsertPersonalProjectAsAsync(Guid personalOwnerId, string role, Guid actingUserId)
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();

        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                do $$ begin
                    if not exists (select 1 from pg_roles where rolname = 'rls_probe') then
                        create role rls_probe nologin nosuperuser nobypassrls;
                    end if;
                end $$;
                grant usage on schema app to rls_probe;
                grant execute on all functions in schema app to rls_probe;
                grant select, insert on all tables in schema public to rls_probe;
                select set_config('app.current_role', @role, false), set_config('app.current_user_id', @uid, false);
                set role rls_probe;
                """;
            setup.Parameters.AddWithValue("role", role);
            setup.Parameters.AddWithValue("uid", actingUserId.ToString());
            await setup.ExecuteNonQueryAsync();
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            insert into projects (id, name, code, status, created_at, personal_owner_id)
            values (@id, 'Personal', @code, 'Active', now(), @owner)
            """;
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("code", $"Q{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}");
        insert.Parameters.AddWithValue("owner", personalOwnerId);
        try
        {
            return await insert.ExecuteNonQueryAsync() == 1;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    /// <summary>Attempts a title update as the unprivileged probe role and returns the rows affected —
    /// RLS filters the target rows, so a denied write is 0 rows, not an exception.</summary>
    private async Task<int> UpdateTaskTitleAsAsync(Guid taskId, string role, Guid userId)
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();

        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                do $$ begin
                    if not exists (select 1 from pg_roles where rolname = 'rls_probe') then
                        create role rls_probe nologin nosuperuser nobypassrls;
                    end if;
                end $$;
                grant usage on schema app to rls_probe;
                grant execute on all functions in schema app to rls_probe;
                grant select, update on all tables in schema public to rls_probe;
                select set_config('app.current_role', @role, false), set_config('app.current_user_id', @uid, false);
                set role rls_probe;
                """;
            setup.Parameters.AddWithValue("role", role);
            setup.Parameters.AddWithValue("uid", userId.ToString());
            await setup.ExecuteNonQueryAsync();
        }

        await using var update = conn.CreateCommand();
        update.CommandText = "update tasks set title = title where id = @id";
        update.Parameters.AddWithValue("id", taskId);
        return await update.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opens a fresh connection (no EF interceptor), switches to a non-superuser probe role (superusers
    /// bypass RLS, so we must act as an unprivileged role to observe the policy), stamps the given RLS
    /// identity onto the session, and counts visible rows of <paramref name="table"/> with the given id.
    /// </summary>
    private async Task<long> CountAsAsync(string table, Guid id, string role, Guid? userId)
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();

        await using (var setup = conn.CreateCommand())
        {
            // Grant the probe role the same surface the runtime pulse_app role gets.
            setup.CommandText = """
                do $$ begin
                    if not exists (select 1 from pg_roles where rolname = 'rls_probe') then
                        create role rls_probe nologin nosuperuser nobypassrls;
                    end if;
                end $$;
                grant usage on schema app to rls_probe;
                grant execute on all functions in schema app to rls_probe;
                grant select on all tables in schema public to rls_probe;
                select set_config('app.current_role', @role, false), set_config('app.current_user_id', @uid, false);
                set role rls_probe;
                """;
            setup.Parameters.AddWithValue("role", role);
            setup.Parameters.AddWithValue("uid", userId?.ToString() ?? string.Empty);
            await setup.ExecuteNonQueryAsync();
        }

        await using var countCmd = conn.CreateCommand();
        // Table name is a fixed test literal, not user input.
        countCmd.CommandText = $"select count(*) from {table} where id = @id";
        countCmd.Parameters.AddWithValue("id", id);
        return (long)(await countCmd.ExecuteScalarAsync())!;
    }
}
