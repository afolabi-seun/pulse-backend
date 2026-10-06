using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 2c: a Google Chat space Pulse is added to belongs to no organization until a head links
/// it with a one-time code ("@Pulse link CODE" in the space). Google's request signing is faked; the events
/// endpoint, linking, org scoping and alert-rule checks are real.
/// </summary>
[Collection("Integration")]
public class GoogleChatLinkingTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _app;

    public GoogleChatLinkingTests(PulseWebApplicationFactory factory) : base(factory)
    {
        _app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleChatRequestVerifier>();
            services.AddSingleton<IGoogleChatRequestVerifier, AlwaysValidVerifier>();
        }));
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_newly_added_space_belongs_to_no_organization_until_linked()
    {
        var space = NewSpaceId();
        var head = await HeadInNewOrganizationAsync();

        await ChatEventAsync(AddedToSpace(space, "Team alerts"));

        (await SpacesVisibleToAsync(head)).Should().NotContain(space);
        (await SpacesVisibleToAsync(await DefaultOrganizationHeadAsync())).Should().NotContain(space,
            "a new space no longer falls to the default organization automatically");
    }

    [Fact]
    public async Task A_head_links_a_space_with_a_one_time_code()
    {
        var space = NewSpaceId();
        var (orgName, head) = (await HeadInNewOrganizationWithNameAsync());
        await ChatEventAsync(AddedToSpace(space, "Team alerts"));
        var code = await IssueLinkCodeAsync(head);

        var reply = await ChatEventAsync(LinkMessage(space, $"link {code}"));

        reply.Should().Contain($"Linked this space to {orgName}");
        (await SpacesVisibleToAsync(head)).Should().Contain(space);
        (await SpacesVisibleToAsync(await DefaultOrganizationHeadAsync())).Should().NotContain(space);

        // Single use.
        (await ChatEventAsync(LinkMessage(NewSpaceId(), $"link {code}"))).Should().Contain("isn't valid or has expired");
    }

    [Fact]
    public async Task A_space_linked_to_one_organization_cannot_be_taken_by_another()
    {
        var space = NewSpaceId();
        var first = await HeadInNewOrganizationAsync();
        var second = await HeadInNewOrganizationAsync();
        await ChatEventAsync(AddedToSpace(space, "Shared"));
        await ChatEventAsync(LinkMessage(space, $"link {await IssueLinkCodeAsync(first)}"));

        var reply = await ChatEventAsync(LinkMessage(space, $"link {await IssueLinkCodeAsync(second)}"));

        reply.Should().Contain("already linked to another organization");
        (await SpacesVisibleToAsync(first)).Should().Contain(space);
        (await SpacesVisibleToAsync(second)).Should().NotContain(space);
    }

    [Fact]
    public async Task Alert_rules_can_target_only_the_organizations_own_linked_spaces_and_unlinking_ends_that()
    {
        var space = NewSpaceId();
        var (orgId, owner) = await HeadInNewOrganizationWithIdAsync();
        var (otherOrgId, other) = await HeadInNewOrganizationWithIdAsync();
        await ChatEventAsync(AddedToSpace(space, "Alerts"));

        (await CreateRuleAsync(owner, await SeedTeamInAsync(orgId), space)).StatusCode
            .Should().NotBe(HttpStatusCode.Created, "the space isn't linked yet");

        await ChatEventAsync(LinkMessage(space, $"link {await IssueLinkCodeAsync(owner)}"));
        (await CreateRuleAsync(owner, await SeedTeamInAsync(orgId), space)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await CreateRuleAsync(other, await SeedTeamInAsync(otherOrgId), space)).StatusCode
            .Should().NotBe(HttpStatusCode.Created, "another organization's space");

        var spaces = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/integrations/google-chat", JsonOpts)).GetProperty("data").GetProperty("spaces");
        var id = spaces.EnumerateArray().Single(s => s.GetProperty("spaceId").GetString() == space).GetProperty("id").GetGuid();
        (await other.DeleteAsync($"/api/v1/integrations/google-chat/spaces/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "an organization can't unlink another's space");
        (await owner.DeleteAsync($"/api/v1/integrations/google-chat/spaces/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SpacesVisibleToAsync(owner)).Should().NotContain(space);
    }

    [Fact]
    public async Task An_expired_code_does_not_link()
    {
        var space = NewSpaceId();
        var head = await HeadInNewOrganizationAsync();
        await ChatEventAsync(AddedToSpace(space, "Alerts"));
        var code = await IssueLinkCodeAsync(head);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlRawAsync("update google_chat_link_codes set expires_at = now() - interval '1 minute'");
        }

        (await ChatEventAsync(LinkMessage(space, $"link {code}"))).Should().Contain("isn't valid or has expired");
        (await SpacesVisibleToAsync(head)).Should().NotContain(space);
    }

    // ── Chat events ──────────────────────────────────────────────────────────

    private static string NewSpaceId() => $"spaces/{Guid.NewGuid():N}"[..20];

    private static object AddedToSpace(string space, string name) =>
        new { type = "ADDED_TO_SPACE", space = new { name = space, displayName = name } };

    private static object LinkMessage(string space, string argumentText) => new
    {
        type = "MESSAGE",
        space = new { name = space, displayName = "Alerts" },
        message = new { text = "@Pulse " + argumentText, argumentText, thread = new { name = space + "/threads/1" } },
    };

    /// <summary>Posts as Google Chat would; returns the text Pulse replies in the space, if any.</summary>
    private async Task<string> ChatEventAsync(object payload)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "faked-google-token");
        var response = await client.PostAsJsonAsync("/api/v1/integrations/google-chat/events", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    // ── Pulse side ───────────────────────────────────────────────────────────

    private static async Task<string> IssueLinkCodeAsync(HttpClient head)
    {
        var response = await head.PostAsync("/api/v1/integrations/google-chat/link-codes", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("data").GetProperty("code").GetString()!;
    }

    private static async Task<List<string>> SpacesVisibleToAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/alert-rules/google-chat-spaces", JsonOpts))
            .GetProperty("data").EnumerateArray().Select(s => s.GetProperty("spaceId").GetString()!).ToList();

    private static Task<HttpResponseMessage> CreateRuleAsync(HttpClient client, Guid teamId, string space) =>
        client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "Blockers", metric = "blockerCount", scopeType = "team", scopeId = teamId, comparator = "greaterThan",
            threshold = 3, deliverInApp = true, deliverEmail = false, deliverWebhook = true,
            webhookUrl = "https://hooks.example.test/x", googleChatSpaceId = space,
        });

    private async Task<HttpClient> DefaultOrganizationHeadAsync()
    {
        var head = await SeedEngineerAsync($"gchat_default_{Guid.NewGuid():N}"[..24] + "@pulse.io", Roles.HeadOfRnD);
        return Authenticated(await LoginTokenAsync(head.Email));
    }

    private async Task<HttpClient> HeadInNewOrganizationAsync() => (await HeadInNewOrganizationWithIdAsync()).Head;

    private async Task<(string Name, HttpClient Head)> HeadInNewOrganizationWithNameAsync()
    {
        var name = $"Chat org {Guid.NewGuid():N}"[..17];
        var (_, head) = await HeadInNewOrganizationWithIdAsync(name);
        return (name, head);
    }

    private async Task<(Guid OrgId, HttpClient Head)> HeadInNewOrganizationWithIdAsync(string? name = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var head = await SeedEngineerAsync($"gchat_head_{suffix}@pulse.io", Roles.HeadOfRnD);
        Guid orgId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Organization.Create(name ?? $"Chat org {suffix}", $"chat-{suffix}");
            db.Organizations.Add(org);
            var engineer = await db.Engineers.SingleAsync(e => e.Id == head.Id);
            db.Entry(engineer).Property(e => e.OrganizationId).CurrentValue = org.Id;
            await db.SaveChangesAsync();
            orgId = org.Id;
        }
        return (orgId, Authenticated(await LoginTokenAsync(head.Email)));
    }

    private HttpClient Authenticated(string token)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedTeamInAsync(Guid orgId)
    {
        var team = await SeedTeamAsync($"Chat team {Guid.NewGuid():N}"[..19]);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(team);
        db.Entry(team).Property(t => t.OrganizationId).CurrentValue = orgId;
        await db.SaveChangesAsync();
        return team.Id;
    }

    private sealed class AlwaysValidVerifier : IGoogleChatRequestVerifier
    {
        public Task<bool> VerifyAsync(string? bearerToken, CancellationToken ct = default) => Task.FromResult(bearerToken is not null);
    }
}
