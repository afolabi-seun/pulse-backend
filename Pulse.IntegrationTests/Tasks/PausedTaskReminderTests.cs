using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>A task paused for a week or more is raised with the team lead, department head and PMO of the team that owns it — and only that kind of pause.</summary>
[Collection("Integration")]
public class PausedTaskReminderTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PausedTaskReminderTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<List<NotificationDto>> NotificationsAsync(string email)
    {
        var client = await AuthenticatedClientAsync(email);
        return (await (await client.GetAsync("/api/v1/notifications?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!.Items.ToList();
    }

    private async Task RunJobAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrganizationJobRunner<PausedTaskReminderJob>>()
            .RunForOrganizationAsync(Pulse.Domain.Organizations.Organization.DefaultId, CancellationToken.None);
    }

    private async Task BackdatePauseAsync(Guid taskId, int days, bool byProject = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var at = DateTime.UtcNow.AddDays(-days);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET paused_at = {at}, paused_by_project = {byProject} WHERE id = {taskId}");
    }

    [Fact]
    public async Task A_week_old_pause_reaches_the_owning_teams_lead_head_and_the_pmo_once()
    {
        var lead = await SeedEngineerAsync("pause_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Pause Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var head = await SeedEngineerAsync("pause_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        await SeedEngineerAsync("pause_pm@pulse.io", Roles.ProjectManager);
        var otherLead = await SeedEngineerAsync("pause_other_lead@pulse.io", Roles.TeamLead);
        var otherTeam = await SeedTeamAsync("Other Pause Team", otherLead.Id, "Design");
        await AssignEngineerToTeamAsync(otherLead.Id, otherTeam.Id);
        var engineer = await SeedEngineerAsync("pause_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Pause Project", team.Id);
        var task = await SeedTaskAsync("Left on hold", project.Id, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("pause_eng@pulse.io");
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pause", new { note = "Waiting on the vendor" })).IsSuccessStatusCode.Should().BeTrue();
        await BackdatePauseAsync(task.Id, days: 14);

        await RunJobAsync();

        foreach (var email in new[] { "pause_lead@pulse.io", "pause_head@pulse.io", "pause_pm@pulse.io" })
            (await NotificationsAsync(email)).Should().ContainSingle(n => n.Kind == "paused_task_reminder", email);
        (await NotificationsAsync("pause_eng@pulse.io")).Should().NotContain(n => n.Kind == "paused_task_reminder", "they paused it");
        (await NotificationsAsync("pause_other_lead@pulse.io")).Should().NotContain(n => n.Kind == "paused_task_reminder", "a different team's lead does not own this work");

        // Straight away again: reminded already, so nothing new.
        await RunJobAsync();
        (await NotificationsAsync("pause_lead@pulse.io")).Count(n => n.Kind == "paused_task_reminder").Should().Be(1);
    }

    [Fact]
    public async Task A_recent_pause_and_a_pause_caused_by_a_project_hold_are_left_alone()
    {
        var lead = await SeedEngineerAsync("pause2_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Pause Team Two", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var engineer = await SeedEngineerAsync("pause2_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Pause Project Two", team.Id);
        var recent = await SeedTaskAsync("Paused yesterday", project.Id, assigneeId: engineer.Id);
        var held = await SeedTaskAsync("Paused by a project hold", project.Id, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("pause2_eng@pulse.io");
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{recent.Id}/pause", new { note = "x" })).IsSuccessStatusCode.Should().BeTrue();
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{held.Id}/pause", new { note = "x" })).IsSuccessStatusCode.Should().BeTrue();
        await BackdatePauseAsync(recent.Id, days: 1);
        await BackdatePauseAsync(held.Id, days: 30, byProject: true);

        await RunJobAsync();

        (await NotificationsAsync("pause2_lead@pulse.io")).Should().NotContain(n => n.Kind == "paused_task_reminder");
    }
}
