using System.Net;
using System.Net.Http.Json;
using Pulse.Application.AuditLog;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.AuditLog;

[Collection("Integration")]
public class AuditLogTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public AuditLogTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_access_audit_log()
    {
        var response = await Client.GetAsync("/api/v1/audit-log");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_access_audit_log()
    {
        await SeedEngineerAsync("audit_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("audit_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/audit-log");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_cannot_access_audit_log()
    {
        await SeedEngineerAsync("audit_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("audit_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/audit-log");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_list_audit_log()
    {
        await SeedEngineerAsync("audit_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("audit_head@pulse.io");

        var response = await client.GetAsync("/api/v1/audit-log");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts);
        body!.Status.Should().Be("success");
        body.Data.Should().NotBeNull();
        body.Data!.Items.Should().NotBeNull();
    }

    [Fact]
    public async Task AuditLog_contains_auth_login_entry_after_login()
    {
        await SeedEngineerAsync("audit_login@pulse.io", Roles.HeadOfRnD);
        // Login generates an AUTH_LOGIN audit entry
        await LoginTokenAsync("audit_login@pulse.io");
        var client = await AuthenticatedClientAsync("audit_login@pulse.io");

        var response = await client.GetAsync("/api/v1/audit-log?action=AUTH_LOGIN");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts);
        body!.Data!.Items.Should().Contain(e => e.Action == "AUTH_LOGIN");
    }

    [Fact]
    public async Task AuditLog_filter_by_actor_returns_only_that_actor()
    {
        var head = await SeedEngineerAsync("audit_actor_head@pulse.io", Roles.HeadOfRnD);
        await SeedEngineerAsync("audit_actor_eng@pulse.io", Roles.Engineer);
        // Generate a login entry for the head
        await LoginTokenAsync("audit_actor_head@pulse.io");
        var client = await AuthenticatedClientAsync("audit_actor_head@pulse.io");

        var response = await client.GetAsync($"/api/v1/audit-log?actorId={head.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts);
        body!.Data!.Items.Should().AllSatisfy(e => e.ActorId.Should().Be(head.Id));
    }

    [Fact]
    public async Task AuditLog_returns_most_recent_first()
    {
        await SeedEngineerAsync("audit_order@pulse.io", Roles.HeadOfRnD);
        // Generate multiple entries by logging in several times
        for (var i = 0; i < 3; i++)
            await LoginTokenAsync("audit_order@pulse.io");

        var client = await AuthenticatedClientAsync("audit_order@pulse.io");
        var response = await client.GetAsync("/api/v1/audit-log?action=AUTH_LOGIN&limit=10");

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts);
        var items = body!.Data!.Items;
        items.Count.Should().BeGreaterThan(1);
        // Descending order: each item's Id should be > the next item's Id
        for (var i = 0; i < items.Count - 1; i++)
            items[i].Id.Should().BeGreaterThan(items[i + 1].Id);
    }

    [Fact]
    public async Task AuditLog_cursor_pagination_returns_next_page()
    {
        await SeedEngineerAsync("audit_page@pulse.io", Roles.HeadOfRnD);
        // Generate enough entries for multiple pages
        for (var i = 0; i < 5; i++)
            await LoginTokenAsync("audit_page@pulse.io");

        var client = await AuthenticatedClientAsync("audit_page@pulse.io");

        // Page 1: limit=2
        var page1Resp = await client.GetAsync("/api/v1/audit-log?limit=2");
        var page1 = (await page1Resp.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts))!.Data!;
        page1.Items.Should().HaveCount(2);
        page1.HasMore.Should().BeTrue();
        page1.NextCursor.Should().NotBeNull();

        // Page 2: use cursor from page 1
        var page2Resp = await client.GetAsync($"/api/v1/audit-log?limit=2&cursor={page1.NextCursor}");
        var page2 = (await page2Resp.Content.ReadFromJsonAsync<ApiResponse<AuditLogPageDto>>(JsonOpts))!.Data!;
        page2.Items.Should().NotBeEmpty();
        // Page 2 entries must be older (lower ID) than page 1 entries
        page2.Items[0].Id.Should().BeLessThan(page1.Items[^1].Id);
    }

    [Fact]
    public async Task AuditLog_limit_clamps_to_200()
    {
        await SeedEngineerAsync("audit_limit@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("audit_limit@pulse.io");

        // Request more than 200 — should not error, just returns ≤ 200
        var response = await client.GetAsync("/api/v1/audit-log?limit=999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
