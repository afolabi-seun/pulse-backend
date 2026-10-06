using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Alerts;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Alerts;

[Collection("Integration")]
public class AlertRulesTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public AlertRulesTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task TeamLead_can_create_list_update_and_delete_a_rule_for_their_own_team()
    {
        var lead = await SeedEngineerAsync("alert_rule_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Team", lead.Id);
        var client = await AuthenticatedClientAsync("alert_rule_lead@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Too many blockers",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 3,
            deliverInApp = true,
            deliverEmail = false,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = (await created.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;
        rule.ScopeName.Should().Be("Alert Rule Team");

        var listResp = await client.GetAsync("/api/v1/alert-rules");
        var list = (await listResp.Content.ReadFromJsonAsync<ApiResponse<List<AlertRuleDto>>>(JsonOpts))!.Data!;
        list.Should().ContainSingle(r => r.Id == rule.Id);

        var updateResp = await client.PatchAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            name = "Renamed",
            comparator = "greaterThan",
            threshold = 5,
            deliverInApp = true,
            deliverEmail = true,
            isActive = true,
        });
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await updateResp.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;
        updated.Name.Should().Be("Renamed");
        updated.DeliverEmail.Should().BeTrue();

        var deleteResp = await client.DeleteAsync($"/api/v1/alert-rules/{rule.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listAfterDelete = (await (await client.GetAsync("/api/v1/alert-rules")).Content.ReadFromJsonAsync<ApiResponse<List<AlertRuleDto>>>(JsonOpts))!.Data!;
        listAfterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task TeamLead_cannot_create_a_rule_for_a_team_they_do_not_lead()
    {
        var lead = await SeedEngineerAsync("alert_rule_outsider@pulse.io", Roles.TeamLead);
        var otherTeam = await SeedTeamAsync("Someone Elses Team", Guid.NewGuid());
        var client = await AuthenticatedClientAsync("alert_rule_outsider@pulse.io");

        var resp = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Watching someone else's team",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = otherTeam.Id,
            comparator = "greaterThan",
            threshold = 3,
            deliverInApp = true,
            deliverEmail = false,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Engineer_role_cannot_reach_the_alert_rules_endpoint_at_all()
    {
        await SeedEngineerAsync("alert_rule_engineer@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("alert_rule_engineer@pulse.io");

        var resp = await client.GetAsync("/api/v1/alert-rules");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Scanner_fires_a_real_notification_when_a_teams_blocker_count_breaches_the_rule()
    {
        var lead = await SeedEngineerAsync("alert_rule_scan_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Scan Team", lead.Id);
        var engineer = await SeedEngineerAsync("alert_rule_scan_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Alert Rule Scan Project");

        // Two blocked tasks assigned to the team's engineer — breaches a "> 1" rule.
        var taskOne = await SeedTaskAsync("Blocked one", project.Id, assigneeId: engineer.Id);
        var taskTwo = await SeedTaskAsync("Blocked two", project.Id, assigneeId: engineer.Id);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var t1 = await db.Tasks.FirstAsync(t => t.Id == taskOne.Id);
            var t2 = await db.Tasks.FirstAsync(t => t.Id == taskTwo.Id);
            t1.FlagBlocker("stuck one", lead.Id);
            t2.FlagBlocker("stuck two", lead.Id);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("alert_rule_scan_lead@pulse.io");
        var created = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Scan test rule",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 1,
            deliverInApp = true,
            deliverEmail = false,
        });
        var rule = (await created.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;

        using (var scope = Factory.Services.CreateScope())
        {
            var scanner = scope.ServiceProvider.GetRequiredService<AlertRuleScanner>();
            await scanner.RunAsync();
        }

        var notificationsResp = await client.GetAsync("/api/v1/notifications");
        var notifications = (await notificationsResp.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<Pulse.Application.Notifications.NotificationDto>>>(JsonOpts))!.Data!;
        notifications.Items.Should().Contain(n => n.Kind == "alert_rule_triggered");

        var reListed = (await (await client.GetAsync("/api/v1/alert-rules")).Content.ReadFromJsonAsync<ApiResponse<List<AlertRuleDto>>>(JsonOpts))!.Data!;
        reListed.Single(r => r.Id == rule.Id).LastTriggeredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_persists_webhook_delivery_fields()
    {
        var lead = await SeedEngineerAsync("alert_rule_webhook_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Webhook Team", lead.Id);
        var client = await AuthenticatedClientAsync("alert_rule_webhook_lead@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Webhook rule",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 3,
            deliverInApp = false,
            deliverEmail = false,
            deliverWebhook = true,
            webhookUrl = "https://hooks.slack.com/services/x/y/z",
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = (await created.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;
        rule.DeliverWebhook.Should().BeTrue();
        rule.WebhookUrl.Should().Be("https://hooks.slack.com/services/x/y/z");
    }

    [Fact]
    public async Task Create_persists_the_Google_Chat_space_id_and_the_Active_toggle_preserves_it()
    {
        var lead = await SeedEngineerAsync("alert_rule_gchat_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Google Chat Team", lead.Id);
        var client = await AuthenticatedClientAsync("alert_rule_gchat_lead@pulse.io");
        // A rule can only target a space linked to its organization (multi-tenancy Phase 2c).
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var space = Pulse.Domain.Alerts.GoogleChatSpace.Create("spaces/AAAAAAAAAAA", "Alerts");
            space.LinkTo(Pulse.Domain.Organizations.Organization.DefaultId);
            db.GoogleChatSpaces.Add(space);
            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Google Chat rule",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 3,
            deliverInApp = false,
            deliverEmail = false,
            deliverWebhook = true,
            webhookUrl = "https://hooks.slack.com/services/x/y/z",
            googleChatSpaceId = "spaces/AAAAAAAAAAA",
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = (await created.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;
        rule.GoogleChatSpaceId.Should().Be("spaces/AAAAAAAAAAA");

        // The Active-toggle update must resend googleChatSpaceId, or UpdateAlertRuleCommand's
        // default (null when omitted) would silently wipe it — same gotcha this page already has
        // for webhookUrl/slackChannel.
        var updateResp = await client.PatchAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            name = rule.Name,
            comparator = rule.Comparator,
            threshold = rule.Threshold,
            deliverInApp = rule.DeliverInApp,
            deliverEmail = rule.DeliverEmail,
            isActive = false,
            deliverWebhook = rule.DeliverWebhook,
            webhookUrl = rule.WebhookUrl,
            googleChatSpaceId = rule.GoogleChatSpaceId,
        });
        var updated = (await updateResp.Content.ReadFromJsonAsync<ApiResponse<AlertRuleDto>>(JsonOpts))!.Data!;
        updated.GoogleChatSpaceId.Should().Be("spaces/AAAAAAAAAAA");
    }

    [Fact]
    public async Task Scanner_records_a_BlockerCount_snapshot_when_a_change_metric_is_watched()
    {
        var lead = await SeedEngineerAsync("alert_rule_snapshot_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Snapshot Team", lead.Id);
        var client = await AuthenticatedClientAsync("alert_rule_snapshot_lead@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Blocker count change rule",
            metric = "blockerCountChange",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 1000, // never breaches — this test only cares that a snapshot gets recorded
            deliverInApp = true,
            deliverEmail = false,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var scope = Factory.Services.CreateScope())
        {
            var scanner = scope.ServiceProvider.GetRequiredService<AlertRuleScanner>();
            await scanner.RunAsync();

            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var snapshot = await db.AlertMetricSnapshots
                .FirstOrDefaultAsync(s => s.Metric == Domain.Alerts.AlertMetric.BlockerCount && s.ScopeId == team.Id);
            snapshot.Should().NotBeNull("watching the Change variant must still snapshot the underlying base metric");
            snapshot!.Value.Should().Be(0);
        }
    }

    [Fact]
    public async Task Create_rejects_webhook_delivery_enabled_with_no_url()
    {
        var lead = await SeedEngineerAsync("alert_rule_webhook_novalid_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Alert Rule Webhook Invalid Team", lead.Id);
        var client = await AuthenticatedClientAsync("alert_rule_webhook_novalid_lead@pulse.io");

        var resp = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Webhook rule without a URL",
            metric = "blockerCount",
            scopeType = "team",
            scopeId = team.Id,
            comparator = "greaterThan",
            threshold = 3,
            deliverInApp = false,
            deliverEmail = false,
            deliverWebhook = true,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
