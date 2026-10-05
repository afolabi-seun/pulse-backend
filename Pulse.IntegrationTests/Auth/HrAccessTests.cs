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
/// HR mirrors Executive's read-only, org-wide, no-team access pattern (see
/// ExecutiveAccessTests), including the delivery internals (tasks, sprints, epics, wiki, comments,
/// estimation) via Roles.IsOrgReadOnlyViewer — originally withheld, opened up on request (see
/// ReadOnlyViewerAccessTests for the full matrix across all three roles). The write-denial tests
/// below still hold.
/// </summary>
[Collection("Integration")]
public class HrAccessTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public HrAccessTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── read access, org-wide, no team of their own ──────────────────────────────

    [Fact]
    public async Task Hr_can_view_the_pmo_report()
    {
        await SeedEngineerAsync("hr_pmo_report@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_pmo_report@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task Hr_can_view_escalations()
    {
        await SeedEngineerAsync("hr_escalations@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_escalations@pulse.io");

        var response = await client.GetAsync("/api/v1/escalations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_view_the_standup_summary()
    {
        await SeedEngineerAsync("hr_standup@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_standup@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/standup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_view_a_project_they_have_no_team_membership_in()
    {
        await SeedEngineerAsync("hr_project@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR-unrelated team");
        var project = await SeedProjectAsync("HR-unrelated project", team.Id);
        var client = await AuthenticatedClientAsync("hr_project@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(project.Id);
    }

    [Fact]
    public async Task Hr_can_list_all_projects()
    {
        await SeedEngineerAsync("hr_project_list@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR list team");
        var project = await SeedProjectAsync("HR list project", team.Id);
        var client = await AuthenticatedClientAsync("hr_project_list@pulse.io");

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        var listed = body!.Data!.Single(p => p.Id == project.Id);
        listed.CanAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Hr_can_view_project_members()
    {
        await SeedEngineerAsync("hr_members@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR members team");
        var project = await SeedProjectAsync("HR members project", team.Id);
        var client = await AuthenticatedClientAsync("hr_members@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/members");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_list_all_users()
    {
        await SeedEngineerAsync("hr_users@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_users@pulse.io");

        var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── delivery internals — originally withheld from HR, now readable ──────────────

    [Fact]
    public async Task Hr_can_view_a_task()
    {
        await SeedEngineerAsync("hr_can_task@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR-excluded team");
        var project = await SeedProjectAsync("HR-excluded project", team.Id);
        var task = await SeedTaskAsync("HR-excluded task", project.Id);
        var client = await AuthenticatedClientAsync("hr_can_task@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_view_a_sprint()
    {
        await SeedEngineerAsync("hr_can_sprint@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR-excluded team 2");
        var sprint = await SeedSprintAsync(team.Id, "HR-excluded sprint");
        var client = await AuthenticatedClientAsync("hr_can_sprint@pulse.io");

        var response = await client.GetAsync($"/api/v1/sprints/{sprint.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_view_epics()
    {
        await SeedEngineerAsync("hr_can_epics@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR-excluded team 3");
        var project = await SeedProjectAsync("HR-excluded epics project", team.Id);
        var client = await AuthenticatedClientAsync("hr_can_epics@pulse.io");

        var response = await client.GetAsync($"/api/v1/epics?projectId={project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── time tracking — an explicit, deliberate exception to write-never ─────────

    [Fact]
    public async Task Hr_can_log_a_manual_time_entry()
    {
        await SeedEngineerAsync("hr_log_time@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_log_time@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "admin",
            hours = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_can_start_and_stop_a_timer()
    {
        await SeedEngineerAsync("hr_timer@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_timer@pulse.io");

        // ActiveTimer only allows Task or Meeting categories (a domain rule, unrelated to role).
        var startResponse = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "meeting" });
        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var stopResponse = await client.PostAsJsonAsync("/api/v1/time-entries/timer/stop", new { });
        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_is_not_flagged_as_missing_a_standup_checkin_even_though_they_can_now_log_time()
    {
        // Regression guard for the CheckInExpected/TimeEntrySubmitter split: granting HR
        // time-tracking access must not silently re-include them in the Standup Digest's
        // Missing list, which is a different population by design.
        var hr = await SeedEngineerAsync("hr_not_missing@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_not_missing@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/standup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<Application.CheckIns.Queries.StandupSummaryDto>>(JsonOpts);
        body!.Data!.MissingEngineers.Should().NotContain(m => m.Id == hr.Id);
    }

    // ── never write, anywhere ─────────────────────────────────────────────────────

    [Fact]
    public async Task Hr_cannot_create_a_task()
    {
        await SeedEngineerAsync("hr_no_create@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR write-guard team");
        var project = await SeedProjectAsync("HR write-guard project", team.Id);
        var client = await AuthenticatedClientAsync("hr_no_create@pulse.io");

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
    public async Task Hr_cannot_create_a_user()
    {
        await SeedEngineerAsync("hr_no_create_user@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("hr_no_create_user@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Should never be created",
            email = "should-never-exist@pulse.io",
            role = Roles.Engineer,
            baselinePoints = 10,
            baselineCycleDays = 5,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hr_cannot_delete_a_project()
    {
        await SeedEngineerAsync("hr_no_delete@pulse.io", Roles.HR);
        var team = await SeedTeamAsync("HR write-guard team 2");
        var project = await SeedProjectAsync("HR write-guard project 2", team.Id);
        var client = await AuthenticatedClientAsync("hr_no_delete@pulse.io");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
