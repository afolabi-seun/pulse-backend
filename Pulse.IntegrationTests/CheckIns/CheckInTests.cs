using System.Net;
using System.Net.Http.Json;
using Pulse.Application.CheckIns;
using Pulse.Application.CheckIns.Queries;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.CheckIns;

[Collection("Integration")]
public class CheckInTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public CheckInTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static object CheckInPayload(int daysOffset = 0) => new
    {
        date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysOffset)).ToString("yyyy-MM-dd"),
        completed = "Finished the auth module",
        plannedNext = "Start on task service",
        blockers = (string?)null
    };

    // ── submit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitCheckIn_returns_200_with_check_in_data()
    {
        await SeedEngineerAsync("checkin_submit@pulse.io");
        var client = await AuthenticatedClientAsync("checkin_submit@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts);
        body!.Data!.Completed.Should().Be("Finished the auth module");
    }

    [Fact]
    public async Task SubmitCheckIn_duplicate_date_updates_the_existing_check_in()
    {
        await SeedEngineerAsync("checkin_dup@pulse.io");
        var client = await AuthenticatedClientAsync("checkin_dup@pulse.io");
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/check-ins", new
        {
            date, completed = "Finished the auth module", plannedNext = "Start on task service", blockers = (string?)null
        });
        var firstBody = await first.Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts);

        var second = await client.PostAsJsonAsync("/api/v1/check-ins", new
        {
            date, completed = "Actually also fixed a bug", plannedNext = "Start on task service", blockers = (string?)null
        });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts);
        secondBody!.Data!.Completed.Should().Be("Actually also fixed a bug");
        secondBody.Data!.Id.Should().Be(firstBody!.Data!.Id);

        var history = await client.GetAsync("/api/v1/check-ins?limit=25");
        var historyBody = await history.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Common.PagedResultDto<CheckInDto>>>(JsonOpts);
        historyBody!.Data!.Items.Count(c => c.Date == DateOnly.Parse(date)).Should().Be(1);
    }

    [Fact]
    public async Task SubmitCheckIn_returns_400_when_completed_is_missing()
    {
        await SeedEngineerAsync("checkin_validation@pulse.io");
        var client = await AuthenticatedClientAsync("checkin_validation@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/check-ins", new
        {
            date = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"),
            plannedNext = "Next work"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmitCheckIn_returns_401_for_unauthenticated()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-4));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCheckIn_returns_200_for_own_check_in()
    {
        await SeedEngineerAsync("checkin_get@pulse.io");
        var client = await AuthenticatedClientAsync("checkin_get@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-5)))
            .Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts))!.Data!;

        var getResp = await client.GetAsync($"/api/v1/check-ins/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResp.Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetCheckIn_returns_403_for_another_engineers_check_in()
    {
        await SeedEngineerAsync("checkin_owner@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("checkin_owner@pulse.io");
        var created = (await (await ownerClient.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-6)))
            .Content.ReadFromJsonAsync<ApiResponse<CheckInDto>>(JsonOpts))!.Data!;

        await SeedEngineerAsync("checkin_snoop@pulse.io");
        var snoopClient = await AuthenticatedClientAsync("checkin_snoop@pulse.io");

        var resp = await snoopClient.GetAsync($"/api/v1/check-ins/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCheckIns_returns_own_history_for_engineer()
    {
        await SeedEngineerAsync("checkin_list@pulse.io");
        var client = await AuthenticatedClientAsync("checkin_list@pulse.io");

        await client.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-6));
        await client.PostAsJsonAsync("/api/v1/check-ins", CheckInPayload(-7));

        var response = await client.GetAsync("/api/v1/check-ins");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<CheckInDto>>>(JsonOpts);
        body!.Data!.Items.Should().HaveCountGreaterOrEqualTo(2);
    }

    // ── status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckInStatus_returns_200_for_pm()
    {
        await SeedEngineerAsync("checkin_status_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("checkin_status_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CheckInStatus_returns_403_for_engineer()
    {
        await SeedEngineerAsync("checkin_status_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("checkin_status_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CheckInStatus_never_flags_a_role_that_cannot_check_in_as_missing()
    {
        // HeadOfPmo caller gets org-wide scope, which includes this other PMO engineer — who was
        // never expected to submit a check-in in the first place, so must never show as missing.
        var pmo = await SeedEngineerAsync("checkin_status_pmo_missing@pulse.io", Roles.HeadOfPmo);
        var caller = await SeedEngineerAsync("checkin_status_pmo_caller@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("checkin_status_pmo_caller@pulse.io");

        var response = await client.GetAsync("/api/v1/check-ins/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CheckInStatusDto>>(JsonOpts);
        body!.Data!.MissingEngineers.Should().NotContain(pmo.Id);
        body.Data.MissingEngineers.Should().NotContain(caller.Id);
    }

    // ── auto check-in on task completion ────────────────────────────────────────

    [Fact]
    public async Task MarkDone_on_two_tasks_in_the_same_project_appends_to_one_checkin()
    {
        var engineer = await SeedEngineerAsync("checkin_auto_same_project@pulse.io");
        var project = await SeedProjectAsync("Auto check-in project");
        var taskOne = await SeedTaskAsync("Fix the login bug", project.Id, assigneeId: engineer.Id);
        var taskTwo = await SeedTaskAsync("Write the onboarding docs", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("checkin_auto_same_project@pulse.io");

        var first = await client.PostAsync($"/api/v1/tasks/{taskOne.Id}/mark-done", null);
        var second = await client.PostAsync($"/api/v1/tasks/{taskTwo.Id}/mark-done", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetAsync("/api/v1/check-ins?limit=25");
        var historyBody = await history.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Common.PagedResultDto<CheckInDto>>>(JsonOpts);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todaysCheckIns = historyBody!.Data!.Items.Where(c => c.Date == today).ToList();

        todaysCheckIns.Should().HaveCount(1);
        todaysCheckIns[0].ProjectId.Should().Be(project.Id);
        todaysCheckIns[0].Completed.Should().Contain("Fix the login bug").And.Contain("Write the onboarding docs");
    }

    private async Task<List<CheckInDto>> TodaysCheckInsAsync(HttpClient client)
    {
        var history = await client.GetAsync("/api/v1/check-ins?limit=25");
        var body = await history.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Common.PagedResultDto<CheckInDto>>>(JsonOpts);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return body!.Data!.Items.Where(c => c.Date == today).ToList();
    }

    [Fact]
    public async Task SendToQa_records_a_checkin_entry_for_the_sender()
    {
        var engineer = await SeedEngineerAsync("checkin_auto_qa@pulse.io");
        var project = await SeedProjectAsync("Auto check-in QA project");
        var task = await SeedTaskAsync("Checkout flow", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("checkin_auto_qa@pulse.io");

        (await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var checkIns = await TodaysCheckInsAsync(client);
        checkIns.Should().ContainSingle(c => c.ProjectId == project.Id)
            .Which.Completed.Should().Contain("Sent to QA: Checkout flow");
    }

    [Fact]
    public async Task Frontend_handoff_records_a_checkin_entry_for_the_backend_engineer()
    {
        var backend = await SeedEngineerAsync("checkin_handoff_be@pulse.io");
        var frontend = await SeedEngineerAsync("checkin_handoff_fe@pulse.io");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(frontend.Id))!.SetDiscipline(Pulse.Domain.Tasks.Discipline.Frontend);
            await db.SaveChangesAsync();
        }
        var project = await SeedProjectAsync("Auto check-in handoff project");
        await SeedProjectMemberAsync(project.Id, backend.Id);
        var client = await AuthenticatedClientAsync("checkin_handoff_be@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Profile page", points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id, assigneeId = backend.Id, requiresFrontendHandoff = true,
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;

        (await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/hand-off-to-frontend", new { frontendAssigneeId = frontend.Id }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var checkIns = await TodaysCheckInsAsync(client);
        checkIns.Should().ContainSingle(c => c.ProjectId == project.Id)
            .Which.Completed.Should().Contain("Handed off to Frontend: Profile page");
    }

    [Fact]
    public async Task MarkDone_on_tasks_in_different_projects_creates_separate_checkins()
    {
        var engineer = await SeedEngineerAsync("checkin_auto_diff_project@pulse.io");
        var projectOne = await SeedProjectAsync("Auto check-in project one");
        var projectTwo = await SeedProjectAsync("Auto check-in project two");
        var taskOne = await SeedTaskAsync("Ship the export feature", projectOne.Id, assigneeId: engineer.Id);
        var taskTwo = await SeedTaskAsync("Patch the timezone bug", projectTwo.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("checkin_auto_diff_project@pulse.io");

        var first = await client.PostAsync($"/api/v1/tasks/{taskOne.Id}/mark-done", null);
        var second = await client.PostAsync($"/api/v1/tasks/{taskTwo.Id}/mark-done", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetAsync("/api/v1/check-ins?limit=25");
        var historyBody = await history.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Common.PagedResultDto<CheckInDto>>>(JsonOpts);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todaysCheckIns = historyBody!.Data!.Items.Where(c => c.Date == today).ToList();

        todaysCheckIns.Should().HaveCount(2);
        todaysCheckIns.Should().Contain(c => c.ProjectId == projectOne.Id && c.Completed.Contains("Ship the export feature"));
        todaysCheckIns.Should().Contain(c => c.ProjectId == projectTwo.Id && c.Completed.Contains("Patch the timezone bug"));
    }
}
