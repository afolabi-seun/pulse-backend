using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.TimeEntries;

/// <summary>The Time Summary drill-down: what an engineer is assigned to next to what they logged time on.</summary>
[Collection("Integration")]
public class EngineerTimeActivityTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public EngineerTimeActivityTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private async Task LogAsync(HttpClient client, object body) =>
        (await client.PostAsJsonAsync("/api/v1/time-entries", body)).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<HttpResponseMessage> ActivityAsync(HttpClient client, Guid engineerId, string? from = null, string? to = null) =>
        await client.GetAsync($"/api/v1/time-entries/engineer-activity?engineerId={engineerId}&from={from ?? Today}&to={to ?? Today}");

    private static async Task<EngineerTimeActivityDto> ReadAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiResponse<EngineerTimeActivityDto>>(JsonOpts))!.Data!;

    [Fact]
    public async Task Shows_assigned_tasks_with_their_hours_the_gaps_first_and_work_that_has_moved_on()
    {
        var engineer = await SeedEngineerAsync("eta_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eta_hr@pulse.io", Roles.HR);
        var project = await SeedProjectAsync("Activity project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var logged = await SeedTaskAsync("Logged on", project.Id, points: 3, dueDaysFromNow: 5, assigneeId: engineer.Id);
        var untouched = await SeedTaskAsync("No time yet", project.Id, points: 3, dueDaysFromNow: 3, assigneeId: engineer.Id);
        var backlog = await SeedTaskAsync("Not started", project.Id, points: 0, dueDaysFromNow: 20, assigneeId: engineer.Id);
        var finished = await SeedTaskAsync("Finished since", project.Id, points: 2, dueDaysFromNow: 9, assigneeId: engineer.Id);

        var eng = await AuthenticatedClientAsync("eta_eng@pulse.io");
        await LogAsync(eng, new { date = Today, category = "task", taskId = logged.Id, hours = 2, note = "Pairing" });
        await LogAsync(eng, new { date = Today, category = "task", taskId = finished.Id, hours = 1 });
        await LogAsync(eng, new { date = Today, category = "meeting", hours = 1.5 });
        await MarkTaskDoneAsync(finished.Id);

        var result = await ReadAsync(await ActivityAsync(await AuthenticatedClientAsync("eta_hr@pulse.io"), engineer.Id));

        result.Assigned.Select(t => t.Title).Should().Equal("No time yet", "Not started", "Logged on");
        result.AssignedWithoutTime.Should().Be(2);
        var withTime = result.Assigned.Single(t => t.TaskId == logged.Id);
        withTime.Hours.Should().Be(2);
        withTime.Entries.Single().Note.Should().Be("Pairing");
        withTime.Status.Should().Be("active");
        withTime.Key.Should().EndWith($"-{logged.TaskNumber}");
        withTime.ProjectName.Should().Be("Activity project");
        result.Assigned.Single(t => t.TaskId == backlog.Id).Status.Should().Be("backlog");
        result.Assigned.Should().NotContain(t => t.TaskId == finished.Id, "a finished task is no longer assigned work");

        result.LoggedOnly.Should().ContainSingle(t => t.TaskId == finished.Id && t.Hours == 1);
        result.OtherTime.Should().ContainSingle(c => c.Category == "meeting" && c.Hours == 1.5m);
    }

    [Fact]
    public async Task Hours_outside_the_period_do_not_count()
    {
        var engineer = await SeedEngineerAsync("eta_period_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eta_period_hr@pulse.io", Roles.HR);
        var project = await SeedProjectAsync("Period project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Logged long ago", project.Id, points: 3, assigneeId: engineer.Id);
        var longAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)).ToString("yyyy-MM-dd");
        await LogAsync(await AuthenticatedClientAsync("eta_period_eng@pulse.io"),
            new { date = longAgo, category = "task", taskId = task.Id, hours = 4 });

        var result = await ReadAsync(await ActivityAsync(await AuthenticatedClientAsync("eta_period_hr@pulse.io"), engineer.Id));

        result.Assigned.Single(t => t.TaskId == task.Id).Hours.Should().Be(0);
        result.AssignedWithoutTime.Should().BeGreaterOrEqualTo(1);
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.HeadOfPmo)]
    public async Task Org_wide_roles_can_view_any_engineers_activity(string role)
    {
        var engineer = await SeedEngineerAsync($"eta_vis_eng_{role}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"eta_vis_{role}@pulse.io", role);

        (await ActivityAsync(await AuthenticatedClientAsync($"eta_vis_{role}@pulse.io"), engineer.Id))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_colleague_or_another_teams_lead_cannot_view_it()
    {
        var engineer = await SeedEngineerAsync("eta_deny_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eta_deny_peer@pulse.io", Roles.Engineer);
        var lead = await SeedEngineerAsync("eta_deny_lead@pulse.io", Roles.TeamLead);
        var otherTeam = await SeedTeamAsync("Elsewhere team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, otherTeam.Id);

        (await ActivityAsync(await AuthenticatedClientAsync("eta_deny_peer@pulse.io"), engineer.Id))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ActivityAsync(await AuthenticatedClientAsync("eta_deny_lead@pulse.io"), engineer.Id))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_team_lead_sees_their_own_teams_engineer()
    {
        var lead = await SeedEngineerAsync("eta_own_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Own team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var engineer = await SeedEngineerAsync("eta_own_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);

        (await ActivityAsync(await AuthenticatedClientAsync("eta_own_lead@pulse.io"), engineer.Id))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_period_is_rejected()
    {
        var engineer = await SeedEngineerAsync("eta_bad_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eta_bad_hr@pulse.io", Roles.HR);
        var hr = await AuthenticatedClientAsync("eta_bad_hr@pulse.io");

        (await ActivityAsync(hr, engineer.Id, from: "2026-10-10", to: "2026-10-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ActivityAsync(hr, engineer.Id, from: "2026-01-01", to: "2026-12-31")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_personal_task_is_anonymous_to_everyone_but_its_owner()
    {
        var owner = await SeedEngineerAsync("eta_personal_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("eta_personal_exec@pulse.io", Roles.Executive);
        var ownerClient = await AuthenticatedClientAsync("eta_personal_hr@pulse.io");
        var created = await ownerClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Confidential salary review", personal = true,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        await LogAsync(ownerClient, new { date = Today, category = "task", taskId = task.Id, hours = 2 });

        var own = await ReadAsync(await ActivityAsync(ownerClient, owner.Id));
        own.Assigned.Single(t => t.TaskId == task.Id).Title.Should().Be("Confidential salary review");

        var seenByExec = await ReadAsync(await ActivityAsync(await AuthenticatedClientAsync("eta_personal_exec@pulse.io"), owner.Id));
        var masked = seenByExec.Assigned.Single(t => t.TaskId == task.Id);
        masked.Title.Should().Be("Personal task");
        masked.Key.Should().BeNull();
        masked.DueDate.Should().BeNull();
        masked.ProjectName.Should().Be("Personal tasks");
        masked.Hours.Should().Be(2);
    }
}
