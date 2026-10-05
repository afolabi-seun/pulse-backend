using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Application.TimeEntries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.TimeEntries;

[Collection("Integration")]
public class TimerTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TimerTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── start / stop round trip ──────────────────────────────────────────────

    [Fact]
    public async Task Start_then_stop_a_meeting_timer_produces_a_time_entry()
    {
        await SeedEngineerAsync("timer_meeting@pulse.io");
        var client = await AuthenticatedClientAsync("timer_meeting@pulse.io");

        var startResp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "meeting" });
        startResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var started = (await startResp.Content.ReadFromJsonAsync<ApiResponse<ActiveTimerDto>>(JsonOpts))!.Data!;
        started.Category.Should().Be("meeting");

        var activeResp = await client.GetAsync("/api/v1/time-entries/timer/active");
        activeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var active = (await activeResp.Content.ReadFromJsonAsync<ApiResponse<ActiveTimerDto?>>(JsonOpts))!.Data;
        active!.Id.Should().Be(started.Id);

        var stopResp = await client.PostAsync("/api/v1/time-entries/timer/stop", null);
        stopResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await stopResp.Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;
        entry.Category.Should().Be("meeting");
        entry.Hours.Should().BeGreaterThan(0);

        var activeAfterStop = await client.GetAsync("/api/v1/time-entries/timer/active");
        var afterStop = (await activeAfterStop.Content.ReadFromJsonAsync<ApiResponse<ActiveTimerDto?>>(JsonOpts))!.Data;
        afterStop.Should().BeNull();
    }

    [Fact]
    public async Task Stop_with_no_running_timer_returns_404()
    {
        await SeedEngineerAsync("timer_no_running@pulse.io");
        var client = await AuthenticatedClientAsync("timer_no_running@pulse.io");

        var resp = await client.PostAsync("/api/v1/time-entries/timer/stop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Starting_a_new_timer_stops_and_logs_the_one_already_running()
    {
        await SeedEngineerAsync("timer_replace@pulse.io");
        var client = await AuthenticatedClientAsync("timer_replace@pulse.io");

        await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "meeting" });
        var secondStart = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "meeting" });
        secondStart.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResp = await client.GetAsync("/api/v1/time-entries?limit=10");
        var list = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts);
        list!.Data!.Items.Should().ContainSingle(e => e.Category == "meeting");
    }

    // ── task timer: activation, grooming, claiming ───────────────────────────

    [Fact]
    public async Task Starting_a_timer_on_an_assigned_groomed_task_leaves_it_active()
    {
        var engineer = await SeedEngineerAsync("timer_task_active@pulse.io");
        var client = await AuthenticatedClientAsync("timer_task_active@pulse.io");
        var project = await SeedProjectAsync("Timer Active Project");
        var task = await SeedTaskAsync("Timer task", project.Id, points: 3, assigneeId: engineer.Id);

        var resp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Starting_a_timer_on_an_unclaimed_groomed_task_in_an_accessible_project_self_assigns_and_activates()
    {
        var team = await SeedTeamAsync("Timer Claim Team");
        var engineer = await SeedEngineerAsync("timer_claim@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var client = await AuthenticatedClientAsync("timer_claim@pulse.io");
        var project = await SeedProjectAsync("Timer Claim Project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Unclaimed groomed task", project.Id, points: 3, assigneeId: null);

        var resp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var taskResp = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var dto = await taskResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        dto!.Data!.AssigneeId.Should().Be(engineer.Id);
        dto.Data!.Status.Should().Be("active");
    }

    [Fact]
    public async Task Starting_a_timer_on_a_task_assigned_to_someone_else_returns_forbidden()
    {
        var team = await SeedTeamAsync("Timer Others Team");
        var owner = await SeedEngineerAsync("timer_owner@pulse.io");
        var other = await SeedEngineerAsync("timer_other@pulse.io");
        await AssignEngineerToTeamAsync(owner.Id, team.Id);
        await AssignEngineerToTeamAsync(other.Id, team.Id);
        var client = await AuthenticatedClientAsync("timer_other@pulse.io");
        var project = await SeedProjectAsync("Timer Others Project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Owned task", project.Id, points: 3, assigneeId: owner.Id);

        var resp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Starting_a_timer_on_an_inaccessible_projects_task_returns_forbidden_or_not_found()
    {
        await SeedEngineerAsync("timer_no_access@pulse.io");
        var client = await AuthenticatedClientAsync("timer_no_access@pulse.io");
        var otherTeam = await SeedTeamAsync("Timer Inaccessible Team");
        var project = await SeedProjectAsync("Timer Inaccessible Project", ownerTeamId: otherTeam.Id);
        var task = await SeedTaskAsync("Inaccessible task", project.Id, points: 3, assigneeId: null);

        var resp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Browsing_and_claiming_unclaimed_work_succeeds_via_an_assigned_task_in_the_project_without_formal_membership()
    {
        // Regression guard: /projects/mine (ListMyProjectsAsync) lists a project the engineer merely
        // has an assigned task in, even without formal membership or being on the owning team — the
        // timer's Unclaimed picker must recognize that same connection, not just IProjectAccessPolicy's
        // narrower membership-or-owning-team check (that mismatch showed "no unclaimed tasks" for a
        // project the picker itself had just offered).
        var ownerTeam = await SeedTeamAsync("Assigned-Elsewhere Owner Team");
        var otherTeam = await SeedTeamAsync("Assigned-Elsewhere Engineer Team");
        var engineer = await SeedEngineerAsync("timer_assigned_elsewhere@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, otherTeam.Id);
        var client = await AuthenticatedClientAsync("timer_assigned_elsewhere@pulse.io");
        var project = await SeedProjectAsync("Assigned-Elsewhere Project", ownerTeamId: ownerTeam.Id);
        // Not a project member, not on the owning team — connected only via this assigned task.
        await SeedTaskAsync("Already assigned here", project.Id, points: 2, assigneeId: engineer.Id);
        var unclaimed = await SeedTaskAsync("Unclaimed groomed task", project.Id, points: 3, assigneeId: null);

        var listResp = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}&noAssignee=true");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        list!.Data!.Items.Should().ContainSingle(t => t.Id == unclaimed.Id);

        var startResp = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = unclaimed.Id });
        startResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var taskResp = await client.GetAsync($"/api/v1/tasks/{unclaimed.Id}");
        var dto = await taskResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        dto!.Data!.AssigneeId.Should().Be(engineer.Id);
    }

    [Fact]
    public async Task Starting_a_timer_on_a_zero_point_task_succeeds_and_leaves_it_in_backlog()
    {
        // Points no longer gate starting a timer — only auto-promotion out of Backlog still
        // requires them, so this succeeds directly and the task stays in Backlog.
        var team = await SeedTeamAsync("Timer Points Team");
        var engineer = await SeedEngineerAsync("timer_points@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var client = await AuthenticatedClientAsync("timer_points@pulse.io");
        var project = await SeedProjectAsync("Timer Points Project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Ungroomed task", project.Id, points: 0, assigneeId: null);

        var start = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        var taskResp = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var dto = await taskResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        dto!.Data!.Status.Should().Be("backlog");

        // Supplying points on a follow-up start still activates it, same as grooming via any
        // other path (Planning Poker, editing the task, etc.).
        var retry = await client.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id, points = 5 });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        var taskResp2 = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var dto2 = await taskResp2.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        dto2!.Data!.Points.Should().Be(5);
        dto2.Data!.Status.Should().Be("active");
    }

    // ── regression guard: decision 6 does not loosen the general endpoint ───

    [Fact]
    public async Task Engineer_still_cannot_PATCH_tasks_directly()
    {
        var team = await SeedTeamAsync("Timer Patch Guard Team");
        var engineer = await SeedEngineerAsync("timer_patch_guard@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var client = await AuthenticatedClientAsync("timer_patch_guard@pulse.io");
        var project = await SeedProjectAsync("Timer Patch Guard Project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Guarded task", project.Id, points: 0, assigneeId: null);

        var resp = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── noAssignee task-list filter ───────────────────────────────────────────

    [Fact]
    public async Task ListTasks_with_noAssignee_returns_only_unclaimed_tasks_in_an_accessible_project()
    {
        var team = await SeedTeamAsync("Timer NoAssignee Team");
        var engineer = await SeedEngineerAsync("timer_noassignee@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var client = await AuthenticatedClientAsync("timer_noassignee@pulse.io");
        var project = await SeedProjectAsync("Timer NoAssignee Project", ownerTeamId: team.Id);
        var unclaimed = await SeedTaskAsync("Unclaimed", project.Id, points: 3, assigneeId: null);
        await SeedTaskAsync("Claimed", project.Id, points: 3, assigneeId: engineer.Id);

        var resp = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}&noAssignee=true");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        body!.Data!.Items.Should().ContainSingle(t => t.Id == unclaimed.Id);
    }

    [Fact]
    public async Task ListTasks_with_noAssignee_and_no_projectId_returns_400()
    {
        await SeedEngineerAsync("timer_noassignee_noproject@pulse.io");
        var client = await AuthenticatedClientAsync("timer_noassignee_noproject@pulse.io");

        var resp = await client.GetAsync("/api/v1/tasks?noAssignee=true");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
