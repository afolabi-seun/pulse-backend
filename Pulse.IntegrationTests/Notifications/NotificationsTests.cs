using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Notifications.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Notifications;

[Collection("Integration")]
public class NotificationsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public NotificationsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNotifications_returns_200_with_empty_inbox_for_new_user()
    {
        await SeedEngineerAsync("notif_list@pulse.io");
        var client = await AuthenticatedClientAsync("notif_list@pulse.io");

        var response = await client.GetAsync("/api/v1/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts);
        body!.Status.Should().Be("success");
    }

    [Fact]
    public async Task ListNotifications_returns_seeded_notifications_for_user()
    {
        var engineer = await SeedEngineerAsync("notif_seeded@pulse.io");
        await SeedNotificationAsync(engineer.Id, "checkin_reminder");
        await SeedNotificationAsync(engineer.Id, "escalation_t1");
        var client = await AuthenticatedClientAsync("notif_seeded@pulse.io");

        var response = await client.GetAsync("/api/v1/notifications");

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts);
        body!.Data!.Items.Should().HaveCountGreaterOrEqualTo(2);
        body.Data.Items.Should().AllSatisfy(n => n.UserId.Should().Be(engineer.Id));
    }

    [Fact]
    public async Task ListNotifications_returns_401_for_unauthenticated()
    {
        var response = await Client.GetAsync("/api/v1/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── mark single read ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkNotificationRead_returns_200_and_sets_readAt()
    {
        var engineer = await SeedEngineerAsync("notif_mark@pulse.io");
        var notif = await SeedNotificationAsync(engineer.Id);
        var client = await AuthenticatedClientAsync("notif_mark@pulse.io");

        var response = await client.PatchAsync($"/api/v1/notifications/{notif.Id}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<NotificationDto>>(JsonOpts);
        body!.Data!.IsRead.Should().BeTrue();
        body.Data.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkNotificationRead_is_idempotent_on_already_read_notification()
    {
        var engineer = await SeedEngineerAsync("notif_idem@pulse.io");
        var notif = await SeedNotificationAsync(engineer.Id);
        var client = await AuthenticatedClientAsync("notif_idem@pulse.io");

        await client.PatchAsync($"/api/v1/notifications/{notif.Id}/read", null);
        var second = await client.PatchAsync($"/api/v1/notifications/{notif.Id}/read", null);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkNotificationRead_returns_403_for_another_users_notification()
    {
        var owner = await SeedEngineerAsync("notif_owner@pulse.io");
        await SeedEngineerAsync("notif_intruder@pulse.io");
        var notif = await SeedNotificationAsync(owner.Id);
        var intruderClient = await AuthenticatedClientAsync("notif_intruder@pulse.io");

        var response = await intruderClient.PatchAsync($"/api/v1/notifications/{notif.Id}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkNotificationRead_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("notif_404@pulse.io");
        var client = await AuthenticatedClientAsync("notif_404@pulse.io");

        var response = await client.PatchAsync($"/api/v1/notifications/{Guid.NewGuid()}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── mark all read ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAllNotificationsRead_returns_204()
    {
        var engineer = await SeedEngineerAsync("notif_markall@pulse.io");
        await SeedNotificationAsync(engineer.Id, "checkin_reminder");
        await SeedNotificationAsync(engineer.Id, "escalation_t3");
        var client = await AuthenticatedClientAsync("notif_markall@pulse.io");

        var response = await client.PostAsync("/api/v1/notifications/read-all", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MarkAllNotificationsRead_marks_all_unread_as_read()
    {
        var engineer = await SeedEngineerAsync("notif_markall_verify@pulse.io");
        await SeedNotificationAsync(engineer.Id, "checkin_reminder");
        await SeedNotificationAsync(engineer.Id, "escalation_overdue");
        var client = await AuthenticatedClientAsync("notif_markall_verify@pulse.io");

        await client.PostAsync("/api/v1/notifications/read-all", null);

        var listResp = await client.GetAsync("/api/v1/notifications?unreadOnly=true");
        var body = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts);
        body!.Data!.Items.Should().BeEmpty("all notifications should be marked read");
    }

    [Fact]
    public async Task MarkAllNotificationsRead_only_affects_current_users_notifications()
    {
        var owner = await SeedEngineerAsync("notif_markall_owner@pulse.io");
        var other = await SeedEngineerAsync("notif_markall_other@pulse.io");
        await SeedNotificationAsync(owner.Id, "checkin_reminder");
        var unreadForOther = await SeedNotificationAsync(other.Id, "escalation_t1");
        var ownerClient = await AuthenticatedClientAsync("notif_markall_owner@pulse.io");
        var otherClient = await AuthenticatedClientAsync("notif_markall_other@pulse.io");

        await ownerClient.PostAsync("/api/v1/notifications/read-all", null);

        // Other user's notification must still be unread
        var otherResp = await otherClient.GetAsync("/api/v1/notifications?unreadOnly=true");
        var body = await otherResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts);
        body!.Data!.Items.Should().Contain(n => n.Id == unreadForOther.Id,
            "mark-all-read on one user must not affect another user's notifications");
    }

    // ── cleanup-orphaned-escalations ─────────────────────────────────────────

    [Fact]
    public async Task CleanupOrphanedEscalations_removes_a_manager_notification_the_recipient_cant_access()
    {
        // A department head with no team assigned is deliberately unscoped ("sees all") in this
        // codebase's access model — the outsider needs a real team in a different department to
        // be genuinely excluded, not just a bare head role.
        var assignee = await SeedEngineerAsync("cleanup_assignee@pulse.io", Roles.Engineer);
        var outsider = await SeedEngineerAsync("cleanup_outsider@pulse.io", Roles.HeadOfDesign);
        var ownerTeam = await SeedTeamAsync("Cleanup Owner Team", department: "Engineering");
        var outsiderTeam = await SeedTeamAsync("Cleanup Outsider Team", department: "Design");
        await AssignEngineerToTeamAsync(outsider.Id, outsiderTeam.Id);
        var project = await SeedProjectAsync("Cleanup project", ownerTeam.Id);
        var task = await SeedTaskAsync("Overdue task to clean up", project.Id, assigneeId: assignee.Id);

        Guid orphanedId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var payload = $$"""{"taskId":"{{task.Id}}","taskTitle":"{{task.Title}}","dueDate":"2026-08-01"}""";
            var orphaned = Notification.Create(outsider.Id, NotificationKind.EscalationOverdue, payload);
            db.Notifications.Add(orphaned);
            await db.SaveChangesAsync();
            orphanedId = orphaned.Id;
        }

        await SeedEngineerAsync("cleanup_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("cleanup_pmo@pulse.io");

        var response = await pmoClient.PostAsync("/api/v1/notifications/cleanup-orphaned-escalations", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CleanupSummaryDto>>(JsonOpts);
        body!.Data!.Deleted.Should().BeGreaterOrEqualTo(1);

        var outsiderClient = await AuthenticatedClientAsync("cleanup_outsider@pulse.io");
        var listResp = await outsiderClient.GetAsync("/api/v1/notifications");
        var listBody = await listResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts);
        listBody!.Data!.Items.Should().NotContain(n => n.Id == orphanedId);
    }

    [Fact]
    public async Task CleanupOrphanedEscalations_returns_403_for_a_role_below_head_of_pmo()
    {
        await SeedEngineerAsync("cleanup_denied@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("cleanup_denied@pulse.io");

        var response = await client.PostAsync("/api/v1/notifications/cleanup-orphaned-escalations", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
