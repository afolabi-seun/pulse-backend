using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Application.Reports;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Auth;

/// <summary>
/// Accountant follows the same read-only, org-wide, no-team access pattern as Executive/HR
/// (see ExecutiveAccessTests, HrAccessTests) via Roles.IsOrgReadOnlyViewer — originally narrower
/// (Projects and Reports/Time Summary only), widened on request to include the delivery internals,
/// the Standup Digest and time logging (see ReadOnlyViewerAccessTests for the full matrix). The
/// write-denial tests below still hold.
/// </summary>
[Collection("Integration")]
public class AccountantAccessTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public AccountantAccessTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── read access, org-wide, no team of their own ──────────────────────────────

    [Fact]
    public async Task Accountant_can_view_a_project_they_have_no_team_membership_in()
    {
        await SeedEngineerAsync("accountant_project@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant-unrelated team");
        var project = await SeedProjectAsync("Accountant-unrelated project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_project@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(project.Id);
    }

    [Fact]
    public async Task Accountant_can_list_all_projects()
    {
        await SeedEngineerAsync("accountant_project_list@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant list team");
        var project = await SeedProjectAsync("Accountant list project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_project_list@pulse.io");

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        var listed = body!.Data!.Single(p => p.Id == project.Id);
        listed.CanAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Accountant_can_view_project_members()
    {
        await SeedEngineerAsync("accountant_members@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant members team");
        var project = await SeedProjectAsync("Accountant members project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_members@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/members");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_can_view_project_throughput()
    {
        await SeedEngineerAsync("accountant_throughput@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant throughput team");
        var project = await SeedProjectAsync("Accountant throughput project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_throughput@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/throughput");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_can_view_the_time_entry_summary()
    {
        await SeedEngineerAsync("accountant_time_summary@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_time_summary@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_can_view_the_pmo_report()
    {
        await SeedEngineerAsync("accountant_pmo_report@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_pmo_report@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task Accountant_can_download_the_pmo_report_as_csv()
    {
        await SeedEngineerAsync("accountant_pmo_csv@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_pmo_csv@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo/csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── delivery internals and standup — originally withheld, now readable ────────

    [Fact]
    public async Task Accountant_can_view_a_task()
    {
        await SeedEngineerAsync("accountant_can_task@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant-excluded team");
        var project = await SeedProjectAsync("Accountant-excluded project", team.Id);
        var task = await SeedTaskAsync("Accountant-excluded task", project.Id);
        var client = await AuthenticatedClientAsync("accountant_can_task@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_can_view_a_sprint()
    {
        await SeedEngineerAsync("accountant_can_sprint@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant-excluded team 2");
        var sprint = await SeedSprintAsync(team.Id, "Accountant-excluded sprint");
        var client = await AuthenticatedClientAsync("accountant_can_sprint@pulse.io");

        var response = await client.GetAsync($"/api/v1/sprints/{sprint.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_can_view_epics()
    {
        await SeedEngineerAsync("accountant_can_epics@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant-excluded team 3");
        var project = await SeedProjectAsync("Accountant-excluded epics project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_can_epics@pulse.io");

        var response = await client.GetAsync($"/api/v1/epics?projectId={project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_cannot_list_users()
    {
        await SeedEngineerAsync("accountant_no_users@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_no_users@pulse.io");

        var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accountant_cannot_view_escalations()
    {
        await SeedEngineerAsync("accountant_no_escalations@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_no_escalations@pulse.io");

        var response = await client.GetAsync("/api/v1/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accountant_can_view_the_standup_summary()
    {
        await SeedEngineerAsync("accountant_can_standup@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_can_standup@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/standup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── logs time, like HR (added on request) ─────────────────────────────────────

    [Fact]
    public async Task Accountant_can_log_a_manual_time_entry()
    {
        await SeedEngineerAsync("accountant_can_log_time@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_can_log_time@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "admin",
            hours = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accountant_is_not_flagged_as_missing_a_standup_checkin()
    {
        // Accountant is excluded from CheckInExpected by design (no daily-standup obligation) —
        // this only matters if a caller other than Accountant can read the digest, so seed an
        // HR caller (who can) to observe the Missing list.
        var accountant = await SeedEngineerAsync("accountant_not_missing@pulse.io", Roles.Accountant);
        await SeedEngineerAsync("accountant_not_missing_hr@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("accountant_not_missing_hr@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/standup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<Application.CheckIns.Queries.StandupSummaryDto>>(JsonOpts);
        body!.Data!.MissingEngineers.Should().NotContain(m => m.Id == accountant.Id);
    }

    // ── never write, anywhere ─────────────────────────────────────────────────────

    [Fact]
    public async Task Accountant_cannot_create_a_task()
    {
        await SeedEngineerAsync("accountant_no_create@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant write-guard team");
        var project = await SeedProjectAsync("Accountant write-guard project", team.Id);
        var client = await AuthenticatedClientAsync("accountant_no_create@pulse.io");

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
    public async Task Accountant_cannot_create_a_user()
    {
        await SeedEngineerAsync("accountant_no_create_user@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("accountant_no_create_user@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Should never be created",
            email = "should-never-exist-2@pulse.io",
            role = Roles.Engineer,
            baselinePoints = 10,
            baselineCycleDays = 5,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accountant_cannot_delete_a_project()
    {
        await SeedEngineerAsync("accountant_no_delete@pulse.io", Roles.Accountant);
        var team = await SeedTeamAsync("Accountant write-guard team 2");
        var project = await SeedProjectAsync("Accountant write-guard project 2", team.Id);
        var client = await AuthenticatedClientAsync("accountant_no_delete@pulse.io");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
