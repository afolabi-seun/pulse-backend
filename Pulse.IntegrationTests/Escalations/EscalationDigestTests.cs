using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Escalations;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Escalations;

/// <summary>An overdue task alerts its assignee straight away; department heads and the PMO are not alerted per task but get one digest alert instead.</summary>
[Collection("Integration")]
public class EscalationDigestTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public EscalationDigestTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<List<NotificationDto>> NotificationsAsync(string email)
    {
        var client = await AuthenticatedClientAsync(email);
        return (await (await client.GetAsync("/api/v1/notifications?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!.Items.ToList();
    }


    /// <summary>Runs a recurring job for one organization, the way the schedule does.</summary>
    private async Task RunAsync<TJob>(Guid organizationId) where TJob : Pulse.Application.Common.Interfaces.IRecurringJob
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrganizationJobRunner<TJob>>().RunForOrganizationAsync(organizationId, CancellationToken.None);
    }

    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_overdue_task_alerts_the_assignee_at_once_and_reaches_heads_and_the_PMO_through_the_digest()
    {
        var assignee = await SeedEngineerAsync("dig_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("dig_pmo@pulse.io", Roles.HeadOfPmo);
        await SeedEngineerAsync("dig_pm@pulse.io", Roles.ProjectManager);
        var head = await SeedEngineerAsync("dig_head@pulse.io", Roles.HeadOfRnD);
        var project = await SeedProjectAsync("Digest project");
        await SeedProjectMemberAsync(project.Id, head.Id);          // a head opens a project they are a member of (or whose department owns it)
        var task = await SeedTaskAsync("Overdue for the digest", project.Id, assigneeId: assignee.Id);

        // Make it genuinely overdue and long-running, so the scanner fires its Overdue stage.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tasks SET due_date = {due}, activated_at = {DateTime.UtcNow.AddDays(-20)} WHERE id = {task.Id}");
        }

        await RunAsync<EscalationScanner>(Organization.DefaultId);

        // The assignee is told immediately; nobody else is alerted about this task one by one.
        var assigneeAlerts = await NotificationsAsync("dig_assignee@pulse.io");
        assigneeAlerts.Should().Contain(n => n.Kind == "escalation_overdue" && n.Payload!.Contains(task.Id.ToString()));
        foreach (var email in new[] { "dig_pmo@pulse.io", "dig_pm@pulse.io", "dig_head@pulse.io" })
            (await NotificationsAsync(email)).Should().NotContain(n => n.Kind == "escalation_overdue", $"{email} gets the digest, not a per-task alert");

        // The digest then gives each of them one alert covering it.
        await RunAsync<EscalationDigestJob>(Organization.DefaultId);

        foreach (var email in new[] { "dig_pmo@pulse.io", "dig_pm@pulse.io", "dig_head@pulse.io" })
        {
            var digest = (await NotificationsAsync(email)).Should().ContainSingle(n => n.Kind == "escalation_digest", email).Subject;
            digest.Payload.Should().Contain("\"overdue\":");
        }

        // Running it again straight away adds nothing: it only covers what fired since the last one.
        await RunAsync<EscalationDigestJob>(Organization.DefaultId);
        (await NotificationsAsync("dig_pmo@pulse.io")).Count(n => n.Kind == "escalation_digest").Should().Be(1);
    }

    [Fact]
    public async Task Another_organizations_PMO_never_gets_a_digest_about_this_organizations_tasks()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid orgB;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Organization.Create("Digest org B", $"dig-b-{suffix}");
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            orgB = org.Id;
        }

        var assignee = await SeedEngineerAsync($"digiso_assignee_{suffix}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"digiso_pmoA_{suffix}@pulse.io", Roles.HeadOfPmo);
        var pmoB = await SeedEngineerAsync($"digiso_pmoB_{suffix}@pulse.io", Roles.HeadOfPmo);
        await MoveToOrganizationAsync(pmoB, orgB);
        var project = await SeedProjectAsync($"Digest isolation {suffix}");
        var task = await SeedTaskAsync("Overdue in organization A", project.Id, assigneeId: assignee.Id);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tasks SET due_date = {due}, activated_at = {DateTime.UtcNow.AddDays(-20)} WHERE id = {task.Id}");
        }

        foreach (var org in new[] { Organization.DefaultId, orgB })
        {
            await RunAsync<EscalationScanner>(org);
            await RunAsync<EscalationDigestJob>(org);
        }

        (await NotificationsAsync($"digiso_pmoA_{suffix}@pulse.io")).Should().Contain(n => n.Kind == "escalation_digest", "control: the task's own organization is told");
        (await NotificationsAsync($"digiso_pmoB_{suffix}@pulse.io")).Should().NotContain(n => n.Kind == "escalation_digest",
            "organization B has no escalating tasks, and must not hear about organization A's");
    }
}
