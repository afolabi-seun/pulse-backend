using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Reports;
using Pulse.Application.TimeEntries;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.TimeEntries;

[Collection("Integration")]
public class TimeEntryTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TimeEntryTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static object MeetingPayload(int daysOffset = 0) => new
    {
        date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysOffset)).ToString("yyyy-MM-dd"),
        category = "meeting",
        hours = 1.5,
        note = "Sprint planning",
    };

    // ── submit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogTimeEntry_returns_200_for_an_eligible_role()
    {
        await SeedEngineerAsync("time_submit@pulse.io");
        var client = await AuthenticatedClientAsync("time_submit@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts);
        body!.Data!.Hours.Should().Be(1.5m);
        body.Data!.Category.Should().Be("meeting");
    }

    [Fact]
    public async Task LogTimeEntry_returns_403_for_head_of_pmo()
    {
        await SeedEngineerAsync("time_submit_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("time_submit_pmo@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LogTimeEntry_returns_403_for_project_manager()
    {
        await SeedEngineerAsync("time_submit_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("time_submit_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LogTimeEntry_returns_403_for_executive()
    {
        await SeedEngineerAsync("time_submit_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("time_submit_exec@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LogTimeEntry_returns_400_for_an_unknown_category()
    {
        await SeedEngineerAsync("time_bad_category@pulse.io");
        var client = await AuthenticatedClientAsync("time_bad_category@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "not-a-real-category",
            hours = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LogTimeEntry_returns_422_when_a_task_category_entry_has_no_task_id()
    {
        await SeedEngineerAsync("time_missing_task@pulse.io");
        var client = await AuthenticatedClientAsync("time_missing_task@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "task",
            hours = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task LogTimeEntry_against_a_task_sums_correctly_in_the_task_summary()
    {
        var engineer = await SeedEngineerAsync("time_task_summary@pulse.io");
        var client = await AuthenticatedClientAsync("time_task_summary@pulse.io");
        var project = await SeedProjectAsync("Time Entry Task Project");
        var task = await SeedTaskAsync("Task with logged time", project.Id, points: 3, assigneeId: engineer.Id);

        await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "task", taskId = task.Id, hours = 2,
        });
        await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "task", taskId = task.Id, hours = 1.5,
        });

        var summaryResp = await client.GetAsync($"/api/v1/time-entries/task-summary/{task.Id}");
        summaryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResp.Content.ReadFromJsonAsync<ApiResponse<TaskTimeSummaryDto>>(JsonOpts);
        summary!.Data!.TotalHoursLogged.Should().Be(3.5m);
    }

    [Fact]
    public async Task ListTimeEntries_still_resolves_the_task_title_after_the_task_is_reassigned_away()
    {
        // My Time keeps a task's row for any week with real logged hours even after the task
        // leaves the viewer's active roster (see MyTimePage's stalePinIds) — the entry itself must
        // still carry the real title in that case, not just whatever's in the viewer's current
        // task list, or the row shows the literal fallback "Task" instead of a name.
        var engineer = await SeedEngineerAsync("time_title_after_reassign@pulse.io");
        var client = await AuthenticatedClientAsync("time_title_after_reassign@pulse.io");
        var project = await SeedProjectAsync("Time Entry Title Project");
        var task = await SeedTaskAsync("Task later reassigned away", project.Id, points: 3, assigneeId: engineer.Id);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = today.ToString("yyyy-MM-dd"), category = "task", taskId = task.Id, hours = 6,
        });

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var other = await SeedEngineerAsync("time_title_after_reassign_other@pulse.io");
            (await db.Tasks.FindAsync(task.Id))!.Assign(other.Id, other.Id);
            await db.SaveChangesAsync();
        }

        var listResp = await client.GetAsync($"/api/v1/time-entries?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts);
        var entry = page!.Data!.Items.Should().ContainSingle(e => e.TaskId == task.Id).Subject;
        entry.TaskTitle.Should().Be("Task later reassigned away");
    }

    // ── get / IDOR ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTimeEntry_returns_200_for_own_entry()
    {
        await SeedEngineerAsync("time_get@pulse.io");
        var client = await AuthenticatedClientAsync("time_get@pulse.io");
        var created = (await (await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload()))
            .Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

        var getResp = await client.GetAsync($"/api/v1/time-entries/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTimeEntry_returns_403_for_another_engineers_entry()
    {
        await SeedEngineerAsync("time_owner@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("time_owner@pulse.io");
        var created = (await (await ownerClient.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload()))
            .Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

        await SeedEngineerAsync("time_snoop@pulse.io");
        var snoopClient = await AuthenticatedClientAsync("time_snoop@pulse.io");

        var resp = await snoopClient.GetAsync($"/api/v1/time-entries/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── update / delete ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTimeEntry_returns_403_when_not_the_owner()
    {
        await SeedEngineerAsync("time_upd_owner@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("time_upd_owner@pulse.io");
        var created = (await (await ownerClient.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload()))
            .Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

        await SeedEngineerAsync("time_upd_snoop@pulse.io");
        var snoopClient = await AuthenticatedClientAsync("time_upd_snoop@pulse.io");

        var resp = await snoopClient.PutAsJsonAsync($"/api/v1/time-entries/{created.Id}", MeetingPayload());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteTimeEntry_returns_403_when_not_the_owner()
    {
        await SeedEngineerAsync("time_del_owner@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("time_del_owner@pulse.io");
        var created = (await (await ownerClient.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload()))
            .Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

        await SeedEngineerAsync("time_del_snoop@pulse.io");
        var snoopClient = await AuthenticatedClientAsync("time_del_snoop@pulse.io");

        var resp = await snoopClient.DeleteAsync($"/api/v1/time-entries/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteTimeEntry_returns_200_for_the_owner_and_removes_it()
    {
        await SeedEngineerAsync("time_del_self@pulse.io");
        var client = await AuthenticatedClientAsync("time_del_self@pulse.io");
        var created = (await (await client.PostAsJsonAsync("/api/v1/time-entries", MeetingPayload()))
            .Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

        var deleteResp = await client.DeleteAsync($"/api/v1/time-entries/{created.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResp = await client.GetAsync($"/api/v1/time-entries/{created.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── summary ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_returns_200_for_team_lead()
    {
        await SeedEngineerAsync("time_summary_lead@pulse.io", Roles.TeamLead);
        var client = await AuthenticatedClientAsync("time_summary_lead@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSummary_returns_200_for_executive()
    {
        await SeedEngineerAsync("time_summary_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("time_summary_exec@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSummary_returns_403_for_engineer()
    {
        await SeedEngineerAsync("time_summary_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("time_summary_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── summary CSV export ───────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.TeamLead)]
    public async Task GetSummaryCsv_returns_200_with_csv_content_type(string role)
    {
        await SeedEngineerAsync($"time_summary_csv_{role}@pulse.io", role);
        var client = await AuthenticatedClientAsync($"time_summary_csv_{role}@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary/csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task GetSummaryCsv_returns_403_for_engineer()
    {
        await SeedEngineerAsync("time_summary_csv_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("time_summary_csv_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary/csv");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSummaryCsv_includes_the_engineers_logged_hours_and_name()
    {
        var engineer = await SeedEngineerAsync("time_summary_csv_data@pulse.io");
        var client = await AuthenticatedClientAsync("time_summary_csv_data@pulse.io");
        await SeedEngineerAsync("time_summary_csv_data_hr@pulse.io", Roles.HR);
        var hrClient = await AuthenticatedClientAsync("time_summary_csv_data_hr@pulse.io");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monday = thisMonday.AddDays(-7);
        var wednesday = monday.AddDays(2);

        var logResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = wednesday.ToString("yyyy-MM-dd"), category = "admin", hours = 4,
        });
        logResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var csvResp = await hrClient.GetAsync($"/api/v1/time-entries/summary/csv?weekOf={monday:yyyy-MM-dd}");
        csvResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await csvResp.Content.ReadAsStringAsync();

        csv.Should().Contain("=== HOURS BY ENGINEER ===");
        csv.Should().Contain(wednesday.ToString("yyyy-MM-dd"));
        csv.Should().Contain("=== HOURS BY PROJECT ===");
        // The shared test database seeds every engineer with the same literal name ("Test User"),
        // so this can't pin down a single row by name — instead, confirm the 4-hour entry landed in
        // *some* engineer row (not merely appears somewhere in the file), the CSV's whole point
        // being per-row attribution.
        csv.Split('\n')
            .Where(l => l.StartsWith(engineer.Name + ","))
            .Should().Contain(l => l.Contains("4.00"));
    }

    [Fact]
    public async Task GetSummary_breaks_hours_out_by_the_day_they_were_logged()
    {
        var engineer = await SeedEngineerAsync("time_summary_daily@pulse.io");
        var client = await AuthenticatedClientAsync("time_summary_daily@pulse.io");
        await SeedEngineerAsync("time_summary_daily_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("time_summary_daily_pmo@pulse.io");

        // Last week's Monday — a full Monday-Sunday week guaranteed to be in the past, since
        // logging time for a future date is rejected.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monday = thisMonday.AddDays(-7);
        var wednesday = monday.AddDays(2);
        var friday = monday.AddDays(4);

        var wedResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = wednesday.ToString("yyyy-MM-dd"), category = "admin", hours = 3,
        });
        wedResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var friResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = friday.ToString("yyyy-MM-dd"), category = "admin", hours = 2,
        });
        friResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var summaryResp = await pmoClient.GetAsync($"/api/v1/time-entries/summary?weekOf={monday:yyyy-MM-dd}");
        summaryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResp.Content.ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts);

        var daily = summary!.Data!.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject.DailyHours;
        daily.Should().HaveCount(7);
        daily.Single(d => d.Date == wednesday).Hours.Should().Be(3);
        daily.Single(d => d.Date == friday).Hours.Should().Be(2);
        daily.Where(d => d.Date != wednesday && d.Date != friday).Should().OnlyContain(d => d.Hours == 0);
    }

    [Fact]
    public async Task GetSummary_with_explicit_from_and_to_spans_an_arbitrary_range_not_aligned_to_a_week()
    {
        var engineer = await SeedEngineerAsync("time_summary_range@pulse.io");
        var client = await AuthenticatedClientAsync("time_summary_range@pulse.io");
        await SeedEngineerAsync("time_summary_range_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("time_summary_range_pmo@pulse.io");

        // A 10-day window spanning two calendar weeks, entirely in the past.
        var to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var from = to.AddDays(-9);
        var midpoint = from.AddDays(5);

        var logResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = midpoint.ToString("yyyy-MM-dd"), category = "admin", hours = 6,
        });
        logResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var summaryResp = await pmoClient.GetAsync($"/api/v1/time-entries/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        summaryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResp.Content.ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts);

        summary!.Data!.WeekOf.Should().Be(from.ToString("yyyy-MM-dd"));
        summary.Data!.To.Should().Be(to.ToString("yyyy-MM-dd"));
        var daily = summary.Data!.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject.DailyHours;
        daily.Should().HaveCount(10);
        daily.Single(d => d.Date == midpoint).Hours.Should().Be(6);
    }

    [Fact]
    public async Task GetSummary_rejects_a_to_before_from()
    {
        await SeedEngineerAsync("time_summary_bad_range@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("time_summary_bad_range@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary?from=2026-09-10&to=2026-09-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.ProjectManager)]
    public async Task GetSummary_daily_breakdown_is_org_wide_for_pmo_roles(string pmoRole)
    {
        // The engineer sits on a team the PMO caller has no relationship to — proving PMO's
        // daily breakdown is org-wide (every active engineer), not scoped by team, the same as
        // the weekly total already is.
        var otherTeam = await SeedTeamAsync($"PMO-unrelated team ({pmoRole})");
        var engineer = await SeedEngineerAsync($"time_summary_daily_pmo_scope_{pmoRole}@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, otherTeam.Id);
        var client = await AuthenticatedClientAsync($"time_summary_daily_pmo_scope_{pmoRole}@pulse.io");
        await SeedEngineerAsync($"time_summary_daily_pmo_caller_{pmoRole}@pulse.io", pmoRole);
        var pmoClient = await AuthenticatedClientAsync($"time_summary_daily_pmo_caller_{pmoRole}@pulse.io");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monday = thisMonday.AddDays(-7);
        var thursday = monday.AddDays(3);

        var logResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = thursday.ToString("yyyy-MM-dd"), category = "admin", hours = 5,
        });
        logResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var summaryResp = await pmoClient.GetAsync($"/api/v1/time-entries/summary?weekOf={monday:yyyy-MM-dd}");
        summaryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResp.Content.ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts);

        var daily = summary!.Data!.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject.DailyHours;
        daily.Should().HaveCount(7);
        daily.Single(d => d.Date == thursday).Hours.Should().Be(5);
    }

    [Fact]
    public async Task GetSummary_excludes_only_roles_that_can_never_log_time_from_the_roster()
    {
        var engineer = await SeedEngineerAsync("time_summary_roster_eng@pulse.io");
        var exec = await SeedEngineerAsync("time_summary_roster_exec@pulse.io", Roles.Executive);
        var pmo = await SeedEngineerAsync("time_summary_roster_pmo@pulse.io", Roles.HeadOfPmo);
        var accountant = await SeedEngineerAsync("time_summary_roster_acct@pulse.io", Roles.Accountant);
        var client = await AuthenticatedClientAsync("time_summary_roster_exec@pulse.io");

        var response = await client.GetAsync("/api/v1/time-entries/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts);
        var ids = body!.Data!.Engineers.Select(e => e.EngineerId).ToList();
        ids.Should().Contain(engineer.Id);
        ids.Should().NotContain(exec.Id);
        ids.Should().NotContain(pmo.Id);
        ids.Should().Contain(accountant.Id, "Accountant can log time (like HR), so it stays on the roster");
    }

    // ── project rollups ──────────────────────────────────────────────────────

    [Fact]
    public async Task Task_and_explicit_project_hours_both_land_in_the_same_project_bucket()
    {
        var engineer = await SeedEngineerAsync("time_project_rollup@pulse.io");
        var client = await AuthenticatedClientAsync("time_project_rollup@pulse.io");
        await SeedEngineerAsync("time_project_rollup_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("time_project_rollup_pmo@pulse.io");
        var project = await SeedProjectAsync("Rollup Project");
        var task = await SeedTaskAsync("Task under rollup project", project.Id, points: 3, assigneeId: engineer.Id);

        var taskResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "task", taskId = task.Id, hours = 2,
        });
        taskResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var meetingResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            category = "meeting", projectId = project.Id, hours = 1.5,
        });
        meetingResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var summaryResp = await pmoClient.GetAsync("/api/v1/time-entries/summary");
        summaryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await summaryResp.Content.ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts);
        var projectBucket = summary!.Data!.Projects.Should().ContainSingle(p => p.ProjectId == project.Id).Subject;
        projectBucket.TotalHours.Should().Be(3.5m);

        var pmoResp = await pmoClient.GetAsync("/api/v1/reports/pmo");
        pmoResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var pmo = await pmoResp.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        var pmoProject = pmo!.Data!.Projects.Should().ContainSingle(p => p.ProjectId == project.Id).Subject;
        pmoProject.HoursLoggedThisWeek.Should().Be(3.5m);
    }
}
