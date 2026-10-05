using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Performance;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Performance;

[Collection("Integration")]
public class PerformanceTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PerformanceTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task GetMyPerformance_does_not_credit_a_QA_reviewers_own_stats_with_the_authors_points()
    {
        var author = await SeedEngineerAsync("perf_qa_author@pulse.io", Roles.Engineer);
        var reviewer = await SeedEngineerAsync("perf_qa_reviewer@pulse.io", Roles.Engineer, isQa: true);
        var project = await SeedProjectAsync("Performance QA Project");
        await SeedProjectMemberAsync(project.Id, author.Id);
        await SeedProjectMemberAsync(project.Id, reviewer.Id);

        var authorClient = await AuthenticatedClientAsync("perf_qa_author@pulse.io");
        var reviewerClient = await AuthenticatedClientAsync("perf_qa_reviewer@pulse.io");

        var task = await SeedTaskAsync("Needs QA", project.Id, points: 5, assigneeId: author.Id, requiresQa: true);

        var sendResp = await authorClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        sendResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = (await sendResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;
        sent.QaTaskId.Should().NotBeNull();

        var qaTaskResp = await reviewerClient.GetAsync($"/api/v1/tasks/{sent.QaTaskId}");
        var qaTask = (await qaTaskResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;
        qaTask.AssigneeId.Should().Be(reviewer.Id, "the IsQa project member should auto-match as reviewer");

        var acceptResp = await reviewerClient.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/mark-done", null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The author's own delivered points reflect the real 5-point feature they built.
        var authorPerf = await authorClient.GetAsync("/api/v1/performance/me");
        var authorBody = await authorPerf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);
        authorBody!.Data!.DeliveredPoints.Should().Be(5);

        // The reviewer's only completed work is the QA sub-task itself — excluded, so their own
        // "points delivered" doesn't also claim the same 5 points the author already has credit
        // for. Reconciles: author (5) + reviewer (0) = the task's real scope (5), not 10.
        var reviewerPerf = await reviewerClient.GetAsync("/api/v1/performance/me");
        var reviewerBody = await reviewerPerf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);
        reviewerBody!.Data!.DeliveredPoints.Should().Be(0);
        reviewerBody.Data.TasksCompleted.Should().Be(0);
    }

    [Fact]
    public async Task GetMyPerformance_checkin_count_is_distinct_days_not_rows()
    {
        // Auto check-in on task completion is scoped per project (Pulse.Application/CheckIns/AutoCheckIn.cs),
        // so completing tasks in two different projects on the same day produces two CheckIn rows for
        // that one day. checkInCount must reflect DAYS checked in, not raw rows, or it can exceed the
        // number of days in the window entirely.
        var engineer = await SeedEngineerAsync("perf_checkin_days@pulse.io", Roles.Engineer);
        var projectOne = await SeedProjectAsync("Perf Checkin Project One");
        var projectTwo = await SeedProjectAsync("Perf Checkin Project Two");
        var taskOne = await SeedTaskAsync("Ship feature A", projectOne.Id, assigneeId: engineer.Id);
        var taskTwo = await SeedTaskAsync("Ship feature B", projectTwo.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("perf_checkin_days@pulse.io");

        (await client.PostAsync($"/api/v1/tasks/{taskOne.Id}/mark-done", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/v1/tasks/{taskTwo.Id}/mark-done", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var perf = await client.GetAsync("/api/v1/performance/me?days=1");
        var body = await perf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);
        body!.Data!.CheckInCount.Should().Be(1);
    }

    [Fact]
    public async Task Executive_can_read_team_and_project_performance_despite_having_no_team()
    {
        var engineer = await SeedEngineerAsync("perf_exec_member@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Perf Exec Team");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Perf Exec Project", team.Id);
        await SeedProjectMemberAsync(project.Id, engineer.Id);

        await SeedEngineerAsync("perf_exec@pulse.io", Roles.Executive);
        var execClient = await AuthenticatedClientAsync("perf_exec@pulse.io");

        var teamResp = await execClient.GetAsync($"/api/v1/performance/team/{team.Id}");
        teamResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var projectResp = await execClient.GetAsync($"/api/v1/performance/project/{project.Id}");
        projectResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TeamPerformance_excludes_a_role_that_can_never_carry_a_delivery_workload()
    {
        var team = await SeedTeamAsync("Perf Workload Team");
        var engineer = await SeedEngineerAsync("perf_workload_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var pmOnTeam = await SeedEngineerAsync("perf_workload_pm@pulse.io", Roles.ProjectManager);
        await AssignEngineerToTeamAsync(pmOnTeam.Id, team.Id);
        var lead = await SeedEngineerAsync("perf_workload_lead@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("perf_workload_lead@pulse.io");

        var response = await client.GetAsync($"/api/v1/performance/team/{team.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<PerformanceMetricsDto>>>(JsonOpts);
        body!.Data!.Select(d => d.EngineerId).Should().Contain(engineer.Id);
        body.Data.Select(d => d.EngineerId).Should().NotContain(pmOnTeam.Id);
    }

    [Fact]
    public async Task GetMyPerformance_does_not_count_a_task_with_no_due_date_as_on_time()
    {
        // SeedTaskAsync always sets a due date, so this task is built inline (mirroring its own
        // construction pattern) to exercise the real TaskRepository.GetPerformanceStatsAsync query
        // — not a mock — against a completed task that never had a deadline to keep.
        var engineer = await SeedEngineerAsync("perf_no_due_date@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Perf No Due Date Project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);

        Guid taskId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var task = PulseTask.Create("No deadline ever set", 3, project.Id);
            var nextTaskNumber = (await db.Tasks.Where(t => t.ProjectId == project.Id)
                .Select(t => (int?)t.TaskNumber).MaxAsync() ?? 0) + 1;
            task.AssignTaskNumber(nextTaskNumber);
            task.Assign(engineer.Id, engineer.Id);
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        var client = await AuthenticatedClientAsync("perf_no_due_date@pulse.io");
        (await client.PostAsync($"/api/v1/tasks/{taskId}/mark-done", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var perf = await client.GetAsync("/api/v1/performance/me");
        var body = await perf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);

        // Completed, but never had a deadline to keep — no punctuality to measure, so this must
        // read as N/A rather than a fabricated 100% (nobody missed a deadline that didn't exist).
        body!.Data!.TasksCompleted.Should().Be(1);
        body.Data.TasksWithDueDate.Should().Be(0);
        body.Data.OnTimeRate.Should().BeNull();
    }

    [Fact]
    public async Task GetMyPerformance_cycle_time_is_never_negative_for_a_task_finished_straight_after_creation()
    {
        var engineer = await SeedEngineerAsync("perf_cycle_same_day@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Perf Cycle Same Day Project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Quick win", project.Id, points: 2, assigneeId: engineer.Id);

        var client = await AuthenticatedClientAsync("perf_cycle_same_day@pulse.io");
        (await client.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var perf = await client.GetAsync("/api/v1/performance/me");
        var body = await perf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);

        // Created and finished on the same calendar day: 0 days, not a fraction below zero.
        body!.Data!.TasksCompleted.Should().Be(1);
        body.Data.AvgCycleTimeDays.Should().Be(0);
    }

    [Fact]
    public async Task GetMyPerformance_survives_a_done_task_whose_end_date_was_cleared()
    {
        var engineer = await SeedEngineerAsync("perf_cycle_no_end@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Perf Cycle No End Date Project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("End date wiped", project.Id, points: 2, assigneeId: engineer.Id);

        var client = await AuthenticatedClientAsync("perf_cycle_no_end@pulse.io");
        (await client.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.Tasks.SingleAsync(t => t.Id == task.Id)).SetActualEndDate(null);
            await db.SaveChangesAsync();
        }

        // Used to throw on the null end date; the day it moved to Done stands in.
        var perf = await client.GetAsync("/api/v1/performance/me");
        perf.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await perf.Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts);
        body!.Data!.AvgCycleTimeDays.Should().BeGreaterThanOrEqualTo(0);
    }
}
