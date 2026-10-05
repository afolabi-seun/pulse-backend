using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Application.TimeEntries;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.TimeEntries;

/// <summary>HR, Executive and Accountant can review which tasks engineers log time against, read-only,
/// while a person's personal tasks stay private to them.</summary>
[Collection("Integration")]
public class TimesheetReviewTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TimesheetReviewTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private async Task<List<TimeEntryDto>> EntriesAsync(HttpClient client, Guid engineerId)
    {
        var resp = await client.GetAsync($"/api/v1/time-entries?engineerId={engineerId}&from={Today}&to={Today}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts))!.Data!.Items.ToList();
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.Accountant)]
    public async Task Org_read_only_roles_can_see_the_tasks_an_engineer_logged_time_against(string role)
    {
        var engineer = await SeedEngineerAsync($"tr_eng_{role}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"tr_viewer_{role}@pulse.io", role);
        var project = await SeedProjectAsync($"Timesheet project {role}");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Fix the login bug", project.Id, points: 3, assigneeId: engineer.Id);
        var engClient = await AuthenticatedClientAsync($"tr_eng_{role}@pulse.io");
        (await engClient.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = Today, category = "task", taskId = task.Id, hours = 2.5, note = "Reproduced and fixed",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var viewer = await AuthenticatedClientAsync($"tr_viewer_{role}@pulse.io");
        var entry = (await EntriesAsync(viewer, engineer.Id)).Should().ContainSingle().Subject;

        entry.TaskTitle.Should().Be("Fix the login bug");
        entry.TaskKey.Should().EndWith($"-{task.TaskNumber}");
        entry.ProjectName.Should().Be($"Timesheet project {role}");
        entry.Hours.Should().Be(2.5m);
        entry.Note.Should().Be("Reproduced and fixed");
    }

    [Fact]
    public async Task An_engineer_still_cannot_view_a_colleagues_time_entries()
    {
        var colleague = await SeedEngineerAsync("tr_colleague@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("tr_nosy@pulse.io", Roles.Engineer);
        var colleagueClient = await AuthenticatedClientAsync("tr_colleague@pulse.io");
        (await colleagueClient.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "admin", hours = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var nosy = await AuthenticatedClientAsync("tr_nosy@pulse.io");

        // Individual contributors are always pinned to their own entries, whatever engineerId they pass.
        var resp = await nosy.GetAsync($"/api/v1/time-entries?engineerId={colleague.Id}&from={Today}&to={Today}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts))!
            .Data!.Items.Should().NotContain(e => e.EngineerId == colleague.Id);
    }

    [Fact]
    public async Task Time_on_a_personal_task_is_anonymous_to_everyone_but_its_owner()
    {
        var owner = await SeedEngineerAsync("tr_personal_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("tr_personal_exec@pulse.io", Roles.Executive);
        await SeedEngineerAsync("tr_personal_pmo@pulse.io", Roles.HeadOfPmo);
        var ownerClient = await AuthenticatedClientAsync("tr_personal_hr@pulse.io");
        var created = await ownerClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Confidential salary review", personal = true,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        (await ownerClient.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = Today, category = "task", taskId = task.Id, hours = 1,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var own = (await EntriesAsync(ownerClient, owner.Id)).Single();
        own.TaskTitle.Should().Be("Confidential salary review", "owners see their own to-dos");

        foreach (var who in new[] { "tr_personal_exec@pulse.io", "tr_personal_pmo@pulse.io" })
        {
            var entry = (await EntriesAsync(await AuthenticatedClientAsync(who), owner.Id)).Single();
            entry.TaskTitle.Should().Be("Personal task");
            entry.TaskKey.Should().BeNull();
            entry.ProjectName.Should().Be("Personal tasks");
        }
    }

    [Fact]
    public async Task The_summarys_project_breakdown_folds_personal_projects_into_one_anonymous_line()
    {
        await SeedEngineerAsync("tr_sum_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("tr_sum_pmo@pulse.io", Roles.HeadOfPmo);
        var hr = await AuthenticatedClientAsync("tr_sum_hr@pulse.io");
        var created = await hr.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Private to-do", personal = true,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        (await hr.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "task", taskId = task.Id, hours = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var pmo = await AuthenticatedClientAsync("tr_sum_pmo@pulse.io");
        var raw = await (await pmo.GetAsync($"/api/v1/time-entries/summary?from={Today}&to={Today}")).Content.ReadAsStringAsync();
        raw.Should().NotContain("— Personal", "a personal project is never named in the breakdown");
        raw.Should().Contain("Personal tasks");
    }
}
