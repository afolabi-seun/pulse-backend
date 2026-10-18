using System.Net;
using System.Net.Http.Json;
using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Meta;
using Pulse.Application.Overwork;
using Pulse.Application.Performance;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Reports.Queries;
using Pulse.Application.Thresholds;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.Infrastructure.Persistence.Migrations;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Earned points follow progress, and are recorded as dated credits whichever way the task moved — set by hand, ticked off a checklist, sent to QA,
/// rejected, re-estimated or sent back to the Backlog.</summary>
[Collection("Integration")]
public class TaskProgressTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskProgressTests(PulseWebApplicationFactory factory) : base(factory) { }

    /// <summary>Loads the task in a fresh scope, lets <paramref name="change"/> act on it (and the context), and saves — the way a command would.</summary>
    private async Task ActAsync(Guid taskId, Func<PulseTask, PulseDbContext, Task> change)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var task = await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId);
        await change(task, db);
        await db.SaveChangesAsync();
    }

    private async Task<(decimal Earned, List<decimal> Credits, int LastProgress)> StateAsync(Guid taskId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var earned = (await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId)).EarnedPoints;
        var credits = await db.TaskProgressCredits.Where(c => c.TaskId == taskId).OrderBy(c => c.CreatedAt).ThenBy(c => c.CreditedAt).ToListAsync();
        return (earned, credits.Select(c => c.Points).ToList(), credits.LastOrDefault()?.ProgressAfter ?? 0);
    }

    private async Task<PulseTask> InFlightTaskAsync(int points, bool requiresQa = false)
    {
        var engineer = await SeedEngineerAsync($"prog_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync($"Progress {Guid.NewGuid():N}");
        return await SeedTaskAsync("Progress task", project.Id, points: points, assigneeId: engineer.Id, requiresQa: requiresQa);
    }

    [Fact]
    public async Task Moving_progress_up_and_down_credits_and_takes_back_exactly_the_difference()
    {
        var task = await InFlightTaskAsync(8);
        (await StateAsync(task.Id)).Earned.Should().Be(0m);

        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(task.Id, (t, _) => { t.SetProgress(75, Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(task.Id, (t, _) => { t.SetProgress(25, Guid.NewGuid()); return Task.CompletedTask; });

        var state = await StateAsync(task.Id);
        state.Credits.Should().Equal(4m, 2m, -4m);
        state.Earned.Should().Be(2m, "8 points at 25%");
        state.Credits.Sum().Should().Be(state.Earned, "earned is always the sum of the credits");
    }

    [Fact]
    public async Task Finishing_a_task_credits_the_rest_so_it_ends_with_all_its_points()
    {
        var task = await InFlightTaskAsync(8);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });

        await ActAsync(task.Id, (t, _) => { t.MarkDone(Guid.NewGuid()); return Task.CompletedTask; });

        var state = await StateAsync(task.Id);
        state.Credits.Should().Equal(4m, 4m);
        state.Earned.Should().Be(8m);
        state.LastProgress.Should().Be(100);
    }

    [Fact]
    public async Task A_qa_rejection_takes_back_what_the_review_stage_had_counted()
    {
        var task = await InFlightTaskAsync(10, requiresQa: true);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(task.Id, (t, _) => { t.SendToQa(Guid.NewGuid()); return Task.CompletedTask; });
        (await StateAsync(task.Id)).Earned.Should().Be(9m, "in QA counts as 90%");

        await ActAsync(task.Id, (t, _) =>
        {
            t.ProposeQaRejection("Not right", Guid.NewGuid(), null);
            t.ConfirmQaRejection(Guid.NewGuid());
            return Task.CompletedTask;
        });

        var state = await StateAsync(task.Id);
        state.Earned.Should().Be(5m, "back in flight at the 50% the engineer had set");
        state.Credits.Should().Equal(5m, 4m, -4m);
        state.Credits.Sum().Should().Be(state.Earned);
    }

    [Fact]
    public async Task Ticking_a_checklist_sets_progress_and_unticking_takes_it_back()
    {
        var task = await InFlightTaskAsync(8);
        Guid first = Guid.Empty;
        await ActAsync(task.Id, (t, db) =>
        {
            var items = Enumerable.Range(1, 4).Select(i => Subtask.Create(t.Id, $"Step {i}", Guid.NewGuid())).ToList();
            first = items[0].Id;
            db.Subtasks.AddRange(items);
            return Task.CompletedTask;
        });
        (await StateAsync(task.Id)).Earned.Should().Be(0m, "a new, unticked checklist is 0%");

        await ActAsync(task.Id, async (t, db) =>
        {
            // By title, not by position: the database does not promise any order.
            foreach (var item in await db.Subtasks.Where(s => s.TaskId == t.Id && (s.Title == "Step 1" || s.Title == "Step 2")).ToListAsync())
                item.SetDone(true, Guid.NewGuid());
        });
        (await StateAsync(task.Id)).Earned.Should().Be(4m, "two of four ticked is 50% of 8");

        await ActAsync(task.Id, async (t, db) => (await db.Subtasks.SingleAsync(s => s.Id == first)).SetDone(false, Guid.NewGuid()));

        var state = await StateAsync(task.Id);
        state.Earned.Should().Be(2m, "one of four is 25% of 8");
        state.Credits.Sum().Should().Be(state.Earned);
    }

    [Fact]
    public async Task A_new_estimate_rescales_what_has_been_earned()
    {
        var task = await InFlightTaskAsync(8);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });

        await ActAsync(task.Id, (t, _) => { t.SetPoints(13, Guid.NewGuid(), "Bigger than thought"); return Task.CompletedTask; });

        var state = await StateAsync(task.Id);
        state.Earned.Should().Be(6.5m);
        state.Credits.Should().Equal(4m, 2.5m);
    }

    [Fact]
    public async Task Returning_a_task_to_the_backlog_takes_back_everything_it_had_earned()
    {
        var task = await InFlightTaskAsync(8);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(75, Guid.NewGuid()); return Task.CompletedTask; });

        await ActAsync(task.Id, (t, _) => { t.ReturnToBacklog(Guid.NewGuid()); return Task.CompletedTask; });

        var state = await StateAsync(task.Id);
        state.Earned.Should().Be(0m);
        state.Credits.Should().Equal(6m, -6m);
    }

    [Fact]
    public async Task Points_are_credited_to_the_engineer_who_would_be_credited_for_the_finished_task()
    {
        var task = await InFlightTaskAsync(4);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var credit = await db.TaskProgressCredits.SingleAsync(c => c.TaskId == task.Id);
        credit.EngineerId.Should().Be(task.AssigneeId);
    }

    [Fact]
    public async Task Changes_that_do_not_move_progress_record_nothing()
    {
        var task = await InFlightTaskAsync(8);

        await ActAsync(task.Id, (t, _) => { t.Pause("hold", Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(task.Id, (t, _) => { t.Resume(Guid.NewGuid()); return Task.CompletedTask; });

        (await StateAsync(task.Id)).Credits.Should().BeEmpty();
    }

    // ── the endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_assignee_can_set_progress_and_the_task_shows_it_with_the_points_earned()
    {
        var engineer = await SeedEngineerAsync("progapi_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Progress API");
        var task = await SeedTaskAsync("Api task", project.Id, points: 8, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("progapi_eng@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 50 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        dto.ProgressPercent.Should().Be(50);
        dto.EffectiveProgress.Should().Be(50);

        var read = (await (await client.GetAsync($"/api/v1/tasks/{task.Id}")).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        read.EarnedPoints.Should().Be(4m);
    }

    [Fact]
    public async Task Someone_with_no_access_cannot_and_100_or_an_odd_step_is_refused()
    {
        var engineer = await SeedEngineerAsync("progapi2_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progapi2_other@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Progress API 2");
        var task = await SeedTaskAsync("Api task 2", project.Id, points: 8, assigneeId: engineer.Id);
        var mine = await AuthenticatedClientAsync("progapi2_eng@pulse.io");
        var other = await AuthenticatedClientAsync("progapi2_other@pulse.io");

        (await other.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 50 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await mine.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 100 })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await mine.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 30 })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_task_with_a_checklist_refuses_a_hand_set_step()
    {
        var engineer = await SeedEngineerAsync("progapi3_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Progress API 3");
        var task = await SeedTaskAsync("Api task 3", project.Id, points: 8, assigneeId: engineer.Id);
        await ActAsync(task.Id, (t, db) => { db.Subtasks.Add(Subtask.Create(t.Id, "One step", Guid.NewGuid())); return Task.CompletedTask; });
        var client = await AuthenticatedClientAsync("progapi3_eng@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 50 });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── automatic check-in ──────────────────────────────────────────────────

    private async Task<List<CheckInDto>> TodaysCheckInsAsync(HttpClient client)
    {
        var body = await (await client.GetAsync("/api/v1/check-ins?limit=25")).Content
            .ReadFromJsonAsync<ApiResponse<PagedResultDto<CheckInDto>>>(JsonOpts);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return body!.Data!.Items.Where(c => c.Date == today).ToList();
    }

    [Fact]
    public async Task Moving_progress_forward_checks_the_engineer_in_with_one_line_per_task()
    {
        var engineer = await SeedEngineerAsync("progci_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Progress check-in");
        var task = await SeedTaskAsync("Fix login", project.Id, points: 8, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("progci_eng@pulse.io");

        foreach (var step in new[] { 25, 50 })
            (await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = step })).StatusCode.Should().Be(HttpStatusCode.OK);

        var checkIn = (await TodaysCheckInsAsync(client)).Should().ContainSingle(c => c.ProjectId == project.Id).Subject;
        checkIn.Completed.Should().Be("Progress: Fix login — 50%");
    }

    [Fact]
    public async Task Ticking_off_checklist_items_checks_the_engineer_in_and_updates_the_same_line()
    {
        var engineer = await SeedEngineerAsync("checkci_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Checklist check-in");
        var task = await SeedTaskAsync("Ship search", project.Id, points: 5, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("checkci_eng@pulse.io");

        var ids = new List<Guid>();
        foreach (var title in new[] { "Index", "Query", "UI" })
        {
            var added = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title });
            ids.Add((await added.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.SubtaskDto>>(JsonOpts))!.Data!.Id);
        }
        foreach (var id in ids.Take(2))
            (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{id}", new { isDone = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var checkIn = (await TodaysCheckInsAsync(client)).Should().ContainSingle(c => c.ProjectId == project.Id).Subject;
        checkIn.Completed.Should().Be("Checklist: Ship search — 2 of 3 items done");
    }

    [Fact]
    public async Task A_lead_setting_someone_elses_progress_does_not_check_the_lead_in()
    {
        var engineer = await SeedEngineerAsync("progci2_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progci2_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Lead progress check-in");
        var task = await SeedTaskAsync("Someone else's", project.Id, points: 8, assigneeId: engineer.Id);
        var pm = await AuthenticatedClientAsync("progci2_pm@pulse.io");

        (await pm.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/progress", new { percent = 50 })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TodaysCheckInsAsync(pm)).Should().BeEmpty();
    }

    // ── reports ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_engineers_throughput_shows_points_earned_next_to_points_delivered()
    {
        var engineer = await SeedEngineerAsync("progrep_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Progress report");
        var inFlight = await SeedTaskAsync("Half done", project.Id, points: 8, assigneeId: engineer.Id);
        var finished = await SeedTaskAsync("Finished", project.Id, points: 5, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("progrep_eng@pulse.io");
        (await client.PostAsJsonAsync($"/api/v1/tasks/{inFlight.Id}/progress", new { percent = 50 })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ActAsync(finished.Id, (t, _) => { t.MarkDone(engineer.Id); return Task.CompletedTask; });

        var weeks = (await (await client.GetAsync($"/api/v1/engineers/{engineer.Id}/throughput")).Content
            .ReadFromJsonAsync<ApiResponse<List<ThroughputWeekDto>>>(JsonOpts))!.Data!;

        weeks.Sum(w => w.PointsDelivered).Should().Be(5, "only the finished task is delivered");
        weeks.Sum(w => w.PointsEarned).Should().Be(9m, "4 from the half-done task and all 5 of the finished one");
    }

    [Fact]
    public async Task The_projects_throughput_shows_points_earned_on_that_project_only()
    {
        var engineer = await SeedEngineerAsync("progproj_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progproj_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Throughput project");
        var other = await SeedProjectAsync("Some other project");
        var mine = await SeedTaskAsync("In this project", project.Id, points: 8, assigneeId: engineer.Id);
        var elsewhere = await SeedTaskAsync("In another project", other.Id, points: 8, assigneeId: engineer.Id);
        await ActAsync(mine.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(elsewhere.Id, (t, _) => { t.SetProgress(75, Guid.NewGuid()); return Task.CompletedTask; });
        var pmo = await AuthenticatedClientAsync("progproj_pmo@pulse.io");

        var weeks = (await (await pmo.GetAsync($"/api/v1/projects/{project.Id}/throughput")).Content
            .ReadFromJsonAsync<ApiResponse<List<ThroughputWeekDto>>>(JsonOpts))!.Data!;

        weeks.Sum(w => w.PointsEarned).Should().Be(4m, "only the 50% of 8 on this project");
        weeks.Sum(w => w.PointsDelivered).Should().Be(0);
    }

    [Fact]
    public async Task The_projects_throughput_starts_from_the_reporting_baseline()
    {
        var engineer = await SeedEngineerAsync("progproj2_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progproj2_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Baseline project");
        var task = await SeedTaskAsync("Done long ago", project.Id, points: 5, assigneeId: engineer.Id);
        await ActAsync(task.Id, (t, _) => { t.MarkDone(engineer.Id); return Task.CompletedTask; });
        var pmo = await AuthenticatedClientAsync("progproj2_pmo@pulse.io");
        async Task<int> DeliveredAsync() => (await (await pmo.GetAsync($"/api/v1/projects/{project.Id}/throughput")).Content
            .ReadFromJsonAsync<ApiResponse<List<ThroughputWeekDto>>>(JsonOpts))!.Data!.Sum(w => w.PointsDelivered);

        try
        {
            (await DeliveredAsync()).Should().Be(5);

            // Baseline tomorrow: the finished task, done today, now falls before it.
            var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
            (await pmo.PutAsync("/api/v1/reports/baseline", JsonContent.Create(new { date = tomorrow.ToString("yyyy-MM-dd") }))).StatusCode.Should().Be(HttpStatusCode.OK);
            (await DeliveredAsync()).Should().Be(0, "work finished before the baseline is not in the chart");
        }
        finally
        {
            await pmo.PutAsync("/api/v1/reports/baseline", JsonContent.Create(new { date = (string?)null }));
        }
        (await DeliveredAsync()).Should().Be(5, "removing the baseline brings it back");
    }

    [Fact]
    public async Task The_organizations_delivery_trend_carries_earned_points_too()
    {
        var engineer = await SeedEngineerAsync("progorg_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progorg_exec@pulse.io", Roles.Executive);
        var project = await SeedProjectAsync("Org trend project");
        var task = await SeedTaskAsync("Moving", project.Id, points: 8, assigneeId: engineer.Id);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(25, Guid.NewGuid()); return Task.CompletedTask; });
        var exec = await AuthenticatedClientAsync("progorg_exec@pulse.io");

        var trend = (await (await exec.GetAsync("/api/v1/reports/org-trend")).Content
            .ReadFromJsonAsync<ApiResponse<OrgTrendDto>>(JsonOpts))!.Data!;

        trend.DeliveryTrend.Sum(w => w.PointsEarned).Should().BeGreaterOrEqualTo(2m, "this task's 25% of 8, among whatever else the shared test server holds");
    }

    [Fact]
    public async Task One_organizations_earned_points_never_appear_in_another_organizations_trend()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid orgB;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Pulse.Domain.Organizations.Organization.Create("Earned org B", $"earned-b-{suffix}");
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            orgB = org.Id;
        }
        var engineer = await SeedEngineerAsync($"earn_a_eng_{suffix}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"earn_a_exec_{suffix}@pulse.io", Roles.Executive);
        var execB = await SeedEngineerAsync($"earn_b_exec_{suffix}@pulse.io", Roles.Executive);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Attach(execB);
            db.Entry(execB).Property("OrganizationId").CurrentValue = orgB;
            await db.SaveChangesAsync();
        }
        var project = await SeedProjectAsync($"Earned isolation {suffix}");
        var task = await SeedTaskAsync("Moving in org A", project.Id, points: 8, assigneeId: engineer.Id);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });

        async Task<decimal> EarnedAsync(string email) =>
            (await (await (await AuthenticatedClientAsync(email)).GetAsync("/api/v1/reports/org-trend")).Content
                .ReadFromJsonAsync<ApiResponse<OrgTrendDto>>(JsonOpts))!.Data!.DeliveryTrend.Sum(w => w.PointsEarned);

        (await EarnedAsync($"earn_a_exec_{suffix}@pulse.io")).Should().BeGreaterOrEqualTo(4m, "control: organization A sees its own earned points");
        (await EarnedAsync($"earn_b_exec_{suffix}@pulse.io")).Should().Be(0m, "organization B has earned nothing and must not see A's");
    }

    // ── the PMO's setting ────────────────────────────────────────────────────

    [Fact]
    public async Task Turning_on_progress_in_workload_lowers_the_load_of_work_that_is_mostly_done_and_turning_it_off_restores_it()
    {
        var engineer = await SeedEngineerAsync("progwl_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progwl_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Progress workload");
        var task = await SeedTaskAsync("Mostly done", project.Id, points: 8, assigneeId: engineer.Id);
        await ActAsync(task.Id, (t, _) => { t.SetProgress(75, Guid.NewGuid()); return Task.CompletedTask; });
        var pmo = await AuthenticatedClientAsync("progwl_pmo@pulse.io");

        async Task<int> ActivePointsAsync() =>
            (await (await pmo.GetAsync($"/api/v1/engineers/{engineer.Id}/signals")).Content
                .ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts))!.Data!.Workload!.ActivePoints;

        try
        {
            (await ActivePointsAsync()).Should().Be(8, "off by default: all the points count");

            (await pmo.PatchAsJsonAsync("/api/v1/thresholds", new { useProgressInWorkload = true })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ActivePointsAsync()).Should().Be(2, "75% of 8 is earned, 2 points are still to do");
            var read = (await (await pmo.GetAsync("/api/v1/thresholds")).Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts))!.Data!;
            read.UseProgressInWorkload.Should().BeTrue();
        }
        finally
        {
            await pmo.PatchAsJsonAsync("/api/v1/thresholds", new { useProgressInWorkload = false });
        }

        (await ActivePointsAsync()).Should().Be(8);
    }

    [Fact]
    public async Task Measuring_velocity_in_earned_points_changes_delivered_points_and_leaves_the_rest_alone()
    {
        var engineer = await SeedEngineerAsync("progvel_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("progvel_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Progress velocity");
        var half = await SeedTaskAsync("Half done", project.Id, points: 8, assigneeId: engineer.Id);
        var finished = await SeedTaskAsync("Finished", project.Id, points: 5, assigneeId: engineer.Id);
        await ActAsync(half.Id, (t, _) => { t.SetProgress(50, Guid.NewGuid()); return Task.CompletedTask; });
        await ActAsync(finished.Id, (t, _) => { t.MarkDone(engineer.Id); return Task.CompletedTask; });
        var eng = await AuthenticatedClientAsync("progvel_eng@pulse.io");
        var pmo = await AuthenticatedClientAsync("progvel_pmo@pulse.io");

        async Task<PerformanceMetricsDto> MineAsync() =>
            (await (await eng.GetAsync("/api/v1/performance/me?days=30")).Content.ReadFromJsonAsync<ApiResponse<PerformanceMetricsDto>>(JsonOpts))!.Data!;
        async Task<bool> MetaFlagAsync() =>
            (await (await pmo.GetAsync("/api/v1/meta")).Content.ReadFromJsonAsync<ApiResponse<AppMetaDto>>(JsonOpts))!.Data!.MeasuresInEarnedPoints;

        try
        {
            var before = await MineAsync();
            before.DeliveredPoints.Should().Be(5, "off by default: only the finished task counts");
            (await MetaFlagAsync()).Should().BeFalse();

            (await pmo.PatchAsJsonAsync("/api/v1/thresholds", new { useEarnedPointsInVelocity = true })).StatusCode.Should().Be(HttpStatusCode.OK);

            var after = await MineAsync();
            after.DeliveredPoints.Should().Be(9, "5 from the finished task plus 4 earned on the half-done one");
            after.TasksCompleted.Should().Be(before.TasksCompleted, "on-time rate, cycle time and the rest still count finished tasks");
            (await MetaFlagAsync()).Should().BeTrue("so screens can say what they are counting");
        }
        finally
        {
            await pmo.PatchAsJsonAsync("/api/v1/thresholds", new { useEarnedPointsInVelocity = false });
        }

        (await MineAsync()).DeliveredPoints.Should().Be(5);
    }

    [Fact]
    public async Task Only_the_PMO_head_can_change_the_setting()
    {
        await SeedEngineerAsync("progwl2_eng@pulse.io", Roles.Engineer);
        var eng = await AuthenticatedClientAsync("progwl2_eng@pulse.io");

        var response = await eng.PatchAsJsonAsync("/api/v1/thresholds", new { useProgressInWorkload = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── the one-off fill-in for tasks already finished ───────────────────────

    [Fact]
    public async Task The_fill_in_credits_finished_tasks_once_and_leaves_work_in_flight_alone()
    {
        var done = await InFlightTaskAsync(5);
        var open = await InFlightTaskAsync(5);
        await ActAsync(done.Id, (t, _) => { t.MarkDone(Guid.NewGuid()); return Task.CompletedTask; });

        // Make the finished task look like one from before this feature: no credits, nothing earned.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"delete from task_progress_credits where task_id = {done.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"update tasks set earned_points = 0 where id = {done.Id}");
            await db.Database.ExecuteSqlRawAsync(AddTaskProgressAndEarnedPoints.BackfillSql);
            await db.Database.ExecuteSqlRawAsync(AddTaskProgressAndEarnedPoints.BackfillSql); // running it again must not double up
        }

        var finished = await StateAsync(done.Id);
        finished.Earned.Should().Be(5m);
        finished.Credits.Should().Equal(5m);
        (await StateAsync(open.Id)).Credits.Should().BeEmpty();
    }
}
