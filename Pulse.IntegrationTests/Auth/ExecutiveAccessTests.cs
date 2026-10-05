using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Escalations;
using Pulse.Application.Projects;
using Pulse.Application.Reports;
using Pulse.Application.Sprints;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Auth;

[Collection("Integration")]
public class ExecutiveAccessTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ExecutiveAccessTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── read access, org-wide, no team of their own ──────────────────────────────

    [Fact]
    public async Task Executive_can_view_the_pmo_report()
    {
        await SeedEngineerAsync("exec_pmo_report@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("exec_pmo_report@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task Executive_can_view_escalations()
    {
        await SeedEngineerAsync("exec_escalations@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("exec_escalations@pulse.io");

        var response = await client.GetAsync("/api/v1/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_can_view_the_standup_summary()
    {
        await SeedEngineerAsync("exec_standup@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("exec_standup@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/standup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_can_view_a_project_they_have_no_team_membership_in()
    {
        await SeedEngineerAsync("exec_project@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec-unrelated team");
        var project = await SeedProjectAsync("Exec-unrelated project", team.Id);
        var client = await AuthenticatedClientAsync("exec_project@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(project.Id);
    }

    [Fact]
    public async Task Executive_can_list_all_projects()
    {
        // Executive could already open any individual project by ID, but had no way to browse the
        // list at all — GET /projects required pm-or-above, which Executive isn't in.
        await SeedEngineerAsync("exec_project_list@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec list team");
        var project = await SeedProjectAsync("Exec list project", team.Id);
        var client = await AuthenticatedClientAsync("exec_project_list@pulse.io");

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        var listed = body!.Data!.Single(p => p.Id == project.Id);
        listed.CanAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Executive_can_view_a_task_in_an_unrelated_project()
    {
        await SeedEngineerAsync("exec_task@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec-unrelated team 2");
        var project = await SeedProjectAsync("Exec-unrelated project 2", team.Id);
        var task = await SeedTaskAsync("Exec-unrelated task", project.Id);
        var client = await AuthenticatedClientAsync("exec_task@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task Executive_can_view_a_sprint_in_an_unrelated_team()
    {
        await SeedEngineerAsync("exec_sprint@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec-unrelated team 3");
        var sprint = await SeedSprintAsync(team.Id, "Exec-unrelated sprint");
        var client = await AuthenticatedClientAsync("exec_sprint@pulse.io");

        var response = await client.GetAsync($"/api/v1/sprints/{sprint.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_sees_sprints_from_teams_they_have_no_membership_in_when_listing()
    {
        await SeedEngineerAsync("exec_sprint_list@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec-unrelated team 4");
        var sprint = await SeedSprintAsync(team.Id, "Exec-unrelated list sprint");
        var client = await AuthenticatedClientAsync("exec_sprint_list@pulse.io");

        var response = await client.GetAsync("/api/v1/sprints");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<SprintDto>>>(JsonOpts);
        body!.Data.Should().Contain(s => s.Id == sprint.Id);
    }

    [Fact]
    public async Task Executive_can_view_epics_in_an_unrelated_project()
    {
        await SeedEngineerAsync("exec_epics@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec epics team");
        var project = await SeedProjectAsync("Exec epics project", team.Id);
        var client = await AuthenticatedClientAsync("exec_epics@pulse.io");

        var response = await client.GetAsync($"/api/v1/epics?projectId={project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_can_view_project_members()
    {
        await SeedEngineerAsync("exec_members@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec members team");
        var project = await SeedProjectAsync("Exec members project", team.Id);
        var client = await AuthenticatedClientAsync("exec_members@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/members");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_can_view_comments_on_an_unrelated_task()
    {
        await SeedEngineerAsync("exec_comments@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec comments team");
        var project = await SeedProjectAsync("Exec comments project", team.Id);
        var task = await SeedTaskAsync("Exec comments task", project.Id);
        var client = await AuthenticatedClientAsync("exec_comments@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}/comments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── never write, anywhere ─────────────────────────────────────────────────────

    [Fact]
    public async Task Executive_cannot_create_a_task()
    {
        await SeedEngineerAsync("exec_no_create@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec write-guard team");
        var project = await SeedProjectAsync("Exec write-guard project", team.Id);
        var client = await AuthenticatedClientAsync("exec_no_create@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Should never be created",
            points = 1,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Executive_cannot_update_a_task()
    {
        await SeedEngineerAsync("exec_no_update@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec write-guard team 2");
        var project = await SeedProjectAsync("Exec write-guard project 2", team.Id);
        var task = await SeedTaskAsync("Exec write-guard task", project.Id);
        var client = await AuthenticatedClientAsync("exec_no_update@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { title = "Hijacked" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Executive_cannot_delete_a_project()
    {
        await SeedEngineerAsync("exec_no_delete@pulse.io", Roles.Executive);
        var team = await SeedTeamAsync("Exec write-guard team 3");
        var project = await SeedProjectAsync("Exec write-guard project 3", team.Id);
        var client = await AuthenticatedClientAsync("exec_no_delete@pulse.io");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── team lead gets escalations too now ────────────────────────────────────────

    [Fact]
    public async Task TeamLead_can_now_view_escalations()
    {
        await SeedEngineerAsync("teamlead_escalations@pulse.io", Roles.TeamLead);
        var client = await AuthenticatedClientAsync("teamlead_escalations@pulse.io");

        var response = await client.GetAsync("/api/v1/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
