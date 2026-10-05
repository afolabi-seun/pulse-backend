using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.TimeEntries;

/// <summary>The "Hours by project" lines of the Time Summary expand into who logged the hours, and on what.</summary>
[Collection("Integration")]
public class ProjectTimeActivityTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ProjectTimeActivityTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private async Task LogAsync(HttpClient client, object body) =>
        (await client.PostAsJsonAsync("/api/v1/time-entries", body)).StatusCode.Should().Be(HttpStatusCode.OK);

    private static async Task<ProjectTimeActivityDto> ReadAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ApiResponse<ProjectTimeActivityDto>>(JsonOpts))!.Data!;
    }

    private Task<HttpResponseMessage> ActivityAsync(HttpClient client, string kind, Guid? projectId = null) =>
        client.GetAsync($"/api/v1/time-entries/project-activity?kind={kind}{(projectId is null ? "" : $"&projectId={projectId}")}&from={Today}&to={Today}");

    private async Task<TimeEntrySummaryDto> SummaryAsync(HttpClient client) =>
        (await (await client.GetAsync($"/api/v1/time-entries/summary?from={Today}&to={Today}")).Content
            .ReadFromJsonAsync<ApiResponse<TimeEntrySummaryDto>>(JsonOpts))!.Data!;

    [Fact]
    public async Task A_project_line_expands_into_tasks_and_people_and_adds_up_to_the_summary()
    {
        var one = await SeedEngineerAsync("pta_one@pulse.io", Roles.Engineer);
        var two = await SeedEngineerAsync("pta_two@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pta_hr@pulse.io", Roles.HR);
        var project = await SeedProjectAsync("Activity breakdown project");
        var elsewhere = await SeedProjectAsync("Some other project");
        foreach (var e in new[] { one, two })
        {
            await SeedProjectMemberAsync(project.Id, e.Id);
            await SeedProjectMemberAsync(elsewhere.Id, e.Id);
        }
        var big = await SeedTaskAsync("Big task", project.Id, points: 3, assigneeId: one.Id);
        var small = await SeedTaskAsync("Small task", project.Id, points: 3, assigneeId: one.Id);
        var other = await SeedTaskAsync("Unrelated task", elsewhere.Id, points: 3, assigneeId: one.Id);
        var c1 = await AuthenticatedClientAsync("pta_one@pulse.io");
        var c2 = await AuthenticatedClientAsync("pta_two@pulse.io");
        await LogAsync(c1, new { date = Today, category = "task", taskId = big.Id, hours = 2 });
        await LogAsync(c2, new { date = Today, category = "task", taskId = big.Id, hours = 1 });
        await LogAsync(c1, new { date = Today, category = "task", taskId = small.Id, hours = 1 });
        await LogAsync(c1, new { date = Today, category = "task", taskId = other.Id, hours = 5 });

        var hr = await AuthenticatedClientAsync("pta_hr@pulse.io");
        var result = await ReadAsync(await ActivityAsync(hr, "project", project.Id));

        result.Name.Should().Be("Activity breakdown project");
        result.TotalHours.Should().Be(4, "the other project's 5h is not part of this line");
        result.Items.Select(i => i.Label).Should().Equal("Big task", "Small task");
        var bigRow = result.Items[0];
        bigRow.Hours.Should().Be(3);
        bigRow.Key.Should().EndWith($"-{big.TaskNumber}");
        bigRow.Status.Should().Be("active");
        bigRow.People.Select(p => (p.Name, p.Hours)).Should().Equal((one.Name, 2m), (two.Name, 1m));
        result.People.Select(p => (p.Name, p.Tasks, p.Hours)).Should().Equal((one.Name, 2, 3m), (two.Name, 1, 1m));

        var line = (await SummaryAsync(hr)).Projects.Single(p => p.ProjectId == project.Id);
        line.Kind.Should().Be("project");
        line.TotalHours.Should().Be(result.TotalHours, "what is expanded adds up to the line it was opened from");
    }

    [Fact]
    public async Task The_general_line_breaks_down_by_category_with_who_logged_each()
    {
        var one = await SeedEngineerAsync("pta_gen_one@pulse.io", Roles.Engineer);
        var two = await SeedEngineerAsync("pta_gen_two@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pta_gen_hr@pulse.io", Roles.HR);
        await LogAsync(await AuthenticatedClientAsync("pta_gen_one@pulse.io"), new { date = Today, category = "meeting", hours = 1.5 });
        await LogAsync(await AuthenticatedClientAsync("pta_gen_two@pulse.io"), new { date = Today, category = "meeting", hours = 0.5 });
        await LogAsync(await AuthenticatedClientAsync("pta_gen_two@pulse.io"), new { date = Today, category = "admin", hours = 1 });

        var hr = await AuthenticatedClientAsync("pta_gen_hr@pulse.io");
        var result = await ReadAsync(await ActivityAsync(hr, "general"));

        result.Name.Should().Be("General");
        var meetings = result.Items.Single(i => i.Label == "Meetings");
        meetings.TaskId.Should().BeNull();
        meetings.People.Select(p => p.Name).Should().Contain(new[] { one.Name, two.Name });
        meetings.Hours.Should().BeGreaterOrEqualTo(2m);
        result.Items.Single(i => i.Label == "Admin").People.Should().Contain(p => p.Name == two.Name && p.Hours == 1);

        var line = (await SummaryAsync(hr)).Projects.Single(p => p.Kind == "general");
        line.TotalHours.Should().Be(result.TotalHours);
    }

    [Fact]
    public async Task The_personal_line_names_people_but_never_task_titles()
    {
        var owner = await SeedEngineerAsync("pta_pers_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pta_pers_exec@pulse.io", Roles.Executive);
        var ownerClient = await AuthenticatedClientAsync("pta_pers_hr@pulse.io");
        var created = await ownerClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Confidential salary review", personal = true,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        await LogAsync(ownerClient, new { date = Today, category = "task", taskId = task.Id, hours = 2 });

        var exec = await AuthenticatedClientAsync("pta_pers_exec@pulse.io");
        var viaKind = await ActivityAsync(exec, "personal");
        var body = await viaKind.Content.ReadAsStringAsync();
        body.Should().NotContain("Confidential salary review");
        var result = await ReadAsync(viaKind);

        result.Name.Should().Be("Personal tasks");
        result.Items.Should().BeEmpty();
        result.People.Should().Contain(p => p.Name == owner.Name && p.Hours == 2);

        // Asking for the personal project by its id is no different.
        var viaId = await ActivityAsync(exec, "project", task.ProjectId);
        (await viaId.Content.ReadAsStringAsync()).Should().NotContain("Confidential salary review");
        (await ReadAsync(await ActivityAsync(exec, "project", task.ProjectId))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_team_lead_sees_only_their_own_teams_people_in_the_breakdown()
    {
        var lead = await SeedEngineerAsync("pta_scope_lead@pulse.io", Roles.TeamLead);
        var teamA = await SeedTeamAsync("Scope team A", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, teamA.Id);
        var teamB = await SeedTeamAsync("Scope team B");
        var mine = await SeedEngineerAsync("pta_scope_mine@pulse.io", Roles.Engineer);
        var theirs = await SeedEngineerAsync("pta_scope_theirs@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(mine.Id, teamA.Id);
        await AssignEngineerToTeamAsync(theirs.Id, teamB.Id);
        var project = await SeedProjectAsync("Scope project", teamA.Id);
        await SeedProjectMemberAsync(project.Id, mine.Id);
        await SeedProjectMemberAsync(project.Id, theirs.Id);
        var task = await SeedTaskAsync("Shared task", project.Id, points: 3, assigneeId: mine.Id);
        await LogAsync(await AuthenticatedClientAsync("pta_scope_mine@pulse.io"), new { date = Today, category = "task", taskId = task.Id, hours = 2 });
        await LogAsync(await AuthenticatedClientAsync("pta_scope_theirs@pulse.io"), new { date = Today, category = "task", taskId = task.Id, hours = 3 });

        var result = await ReadAsync(await ActivityAsync(await AuthenticatedClientAsync("pta_scope_lead@pulse.io"), "project", project.Id));

        result.People.Select(p => p.Name).Should().Equal(mine.Name);
        result.TotalHours.Should().Be(2);
    }

    [Fact]
    public async Task Bad_requests_and_unauthorised_callers_are_refused()
    {
        await SeedEngineerAsync("pta_bad_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pta_bad_eng@pulse.io", Roles.Engineer);
        var hr = await AuthenticatedClientAsync("pta_bad_hr@pulse.io");

        (await ActivityAsync(hr, "nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ActivityAsync(hr, "project")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a project needs an id");
        (await ActivityAsync(hr, "project", Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await hr.GetAsync("/api/v1/time-entries/project-activity?kind=general&from=2026-01-01&to=2026-12-31"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ActivityAsync(await AuthenticatedClientAsync("pta_bad_eng@pulse.io"), "general")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
