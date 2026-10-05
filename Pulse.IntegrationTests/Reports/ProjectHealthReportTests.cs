using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Reports;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Reports;

/// <summary>The Health pill in the PMO Project Health table and the weekly Workstream table comes with its
/// reasons and next steps — see <see cref="ProjectHealthCalculator"/>.</summary>
[Collection("Integration")]
public class ProjectHealthReportTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ProjectHealthReportTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task SqlAsync(string sql, params object[] args)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql, args);
    }

    /// <summary>An active task, made to look like it was activated <paramref name="activatedDaysAgo"/> days ago.</summary>
    private async Task<Guid> ActiveTaskAsync(string title, Guid projectId, Guid assigneeId, int dueInDays, int activatedDaysAgo)
    {
        var task = await SeedTaskAsync(title, projectId, points: 3, dueDaysFromNow: dueInDays, assigneeId: assigneeId);
        // Assign() pulls a lapsed due date up to today, so the real (possibly past) one is set afterwards.
        await SqlAsync(
            "UPDATE tasks SET activated_at = now() - make_interval(days => {0}), due_date = (current_date + make_interval(days => {1}))::date WHERE id = {2}",
            activatedDaysAgo, dueInDays, task.Id);
        return task.Id;
    }

    private async Task BlockAsync(Guid taskId, Guid actorId, int blockedDaysAgo)
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var task = await db.Tasks.FirstAsync(t => t.Id == taskId);
            task.FlagBlocker("Waiting on the vendor API", actorId);
            await db.SaveChangesAsync();
        }
        await SqlAsync(
            "UPDATE task_history SET ts = now() - make_interval(days => {0}) WHERE task_id = {1} AND field = 'status' AND to_value = 'Blocked'",
            blockedDaysAgo, taskId);
    }

    private async Task<ProjectHealthDto> PmoHealthAsync(HttpClient pmo, Guid projectId)
    {
        var report = (await (await pmo.GetAsync("/api/v1/reports/pmo")).Content
            .ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts))!.Data!;
        return report.Projects.Single(p => p.ProjectId == projectId);
    }

    [Fact]
    public async Task A_project_with_overdue_work_a_long_blocker_and_something_due_soon_is_critical_and_explains_why()
    {
        await SeedEngineerAsync("ph_pmo@pulse.io", Roles.HeadOfPmo);
        var lead = await SeedEngineerAsync("ph_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Health team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var engineer = await SeedEngineerAsync("ph_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Health project", team.Id);
        await ActiveTaskAsync("Late migration", project.Id, engineer.Id, dueInDays: -3, activatedDaysAgo: 10);
        var blocked = await ActiveTaskAsync("Waiting on vendor", project.Id, engineer.Id, dueInDays: 30, activatedDaysAgo: 12);
        await BlockAsync(blocked, engineer.Id, blockedDaysAgo: 10);
        await ActiveTaskAsync("Release notes", project.Id, engineer.Id, dueInDays: 2, activatedDaysAgo: 10);

        var health = await PmoHealthAsync(await AuthenticatedClientAsync("ph_pmo@pulse.io"), project.Id);

        health.Health.Should().Be("Critical");
        health.OverdueTasks.Should().Be(1);
        health.DueSoonTasks.Should().Be(1);
        health.Reasons!.Select(r => r.Kind).Should().Equal("overdue", "blocked_long", "due_soon");
        var overdue = health.Reasons!.First(r => r.Kind == "overdue");
        overdue.Examples.Single().Should().Match<HealthTaskRef>(e =>
            e.Title == "Late migration" && e.AssigneeName == engineer.Name && e.Days >= 1 && e.Key != null);
        health.Reasons!.First(r => r.Kind == "blocked_long").Examples.Single().Days.Should().BeGreaterOrEqualTo(5);
        health.NextSteps!.Should().HaveCount(3);

        // The weekly workstream table uses the same rule and the same explanation.
        var leadClient = await AuthenticatedClientAsync("ph_lead@pulse.io");
        var weekly = (await (await leadClient.GetAsync("/api/v1/reports/weekly")).Content
            .ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts))!.Data!;
        var workstream = weekly.Workstreams.Single(w => w.ProjectId == project.Id);
        workstream.Health.Should().Be("Critical");
        workstream.Reasons!.Select(r => r.Kind).Should().Equal("overdue", "blocked_long", "due_soon");
    }

    [Fact]
    public async Task A_fresh_blocker_is_at_risk_and_a_clean_project_is_healthy()
    {
        await SeedEngineerAsync("ph2_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Health team 2");
        var engineer = await SeedEngineerAsync("ph2_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var atRisk = await SeedProjectAsync("Health at risk", team.Id);
        var clean = await SeedProjectAsync("Health clean", team.Id);
        var blocked = await ActiveTaskAsync("Just got stuck", atRisk.Id, engineer.Id, dueInDays: 30, activatedDaysAgo: 3);
        await BlockAsync(blocked, engineer.Id, blockedDaysAgo: 1);
        await ActiveTaskAsync("On track", clean.Id, engineer.Id, dueInDays: 30, activatedDaysAgo: 1);

        var pmo = await AuthenticatedClientAsync("ph2_pmo@pulse.io");
        var risky = await PmoHealthAsync(pmo, atRisk.Id);
        var fine = await PmoHealthAsync(pmo, clean.Id);

        risky.Health.Should().Be("AtRisk");
        risky.Reasons!.Should().ContainSingle(r => r.Kind == "blocked");
        fine.Health.Should().Be("Healthy");
        fine.Reasons.Should().BeEmpty();
        fine.NextSteps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_a_day_or_two_late_is_at_risk_but_one_a_week_late_is_critical()
    {
        await SeedEngineerAsync("ph3_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Health team 3");
        var engineer = await SeedEngineerAsync("ph3_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var barelyLate = await SeedProjectAsync("Health barely late", team.Id);
        var longLate = await SeedProjectAsync("Health long late", team.Id);
        // The project's only task, one day late: the old rule called this Critical (1 of 1 in flight).
        await ActiveTaskAsync("The only task, a day late", barelyLate.Id, engineer.Id, dueInDays: -1, activatedDaysAgo: 10);
        await ActiveTaskAsync("Late for a fortnight", longLate.Id, engineer.Id, dueInDays: -14, activatedDaysAgo: 20);

        var pmo = await AuthenticatedClientAsync("ph3_pmo@pulse.io");
        var gentle = await PmoHealthAsync(pmo, barelyLate.Id);
        var harsh = await PmoHealthAsync(pmo, longLate.Id);

        gentle.Health.Should().Be("AtRisk");
        gentle.Reasons!.Single().Text.Should().Contain("oldest 1 working day late");
        harsh.Health.Should().Be("Critical");
        harsh.Reasons!.Single().Examples.Single().Days.Should().BeGreaterOrEqualTo(5);
        harsh.Reasons!.Single().Action.Should().Be("Finish, re-date or reassign");
    }

    [Fact]
    public async Task The_weekly_table_marks_a_shared_project_as_this_teams_tasks_only()
    {
        var leadB = await SeedEngineerAsync("ph5_lead@pulse.io", Roles.TeamLead);
        var teamA = await SeedTeamAsync("Health owner team");
        var teamB = await SeedTeamAsync("Health visiting team", leadB.Id);
        await AssignEngineerToTeamAsync(leadB.Id, teamB.Id);
        var engineerB = await SeedEngineerAsync("ph5_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineerB.Id, teamB.Id);
        var shared = await SeedProjectAsync("Health shared", teamA.Id);
        var own = await SeedProjectAsync("Health own", teamB.Id);
        await ActiveTaskAsync("Visiting work", shared.Id, engineerB.Id, dueInDays: 20, activatedDaysAgo: 1);
        await ActiveTaskAsync("Own work", own.Id, engineerB.Id, dueInDays: 20, activatedDaysAgo: 1);

        var weekly = (await (await (await AuthenticatedClientAsync("ph5_lead@pulse.io")).GetAsync("/api/v1/reports/weekly")).Content
            .ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts))!.Data!;

        weekly.Workstreams.Single(w => w.ProjectId == shared.Id).TeamSliceOnly.Should().BeTrue();
        weekly.Workstreams.Single(w => w.ProjectId == own.Id).TeamSliceOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Blockers_in_the_pmo_and_leadership_reports_are_aged_from_when_they_were_blocked()
    {
        await SeedEngineerAsync("ph4_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Health team 4");
        var engineer = await SeedEngineerAsync("ph4_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Health blockers", team.Id);
        // Activated a month ago, but only blocked ten days ago.
        var taskId = await ActiveTaskAsync("Blocked recently", project.Id, engineer.Id, dueInDays: 30, activatedDaysAgo: 30);
        await BlockAsync(taskId, engineer.Id, blockedDaysAgo: 10);

        var pmo = await AuthenticatedClientAsync("ph4_pmo@pulse.io");
        var pmoReport = (await (await pmo.GetAsync("/api/v1/reports/pmo")).Content
            .ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts))!.Data!;
        var leadership = (await (await pmo.GetAsync("/api/v1/reports/leadership")).Content
            .ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts))!.Data!;

        pmoReport.BlockerAging.Single(b => b.TaskId == taskId).DaysBlocked.Should().BeInRange(9, 10);
        leadership.Blockers.Single(b => b.TaskId == taskId).DaysBlocked.Should().BeInRange(9, 10);
    }
}
