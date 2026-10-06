using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// Multi-tenancy Phase 2b: an organization's head connects the org's own Slack workspace through Slack's
/// OAuth install. Slack's code exchange is faked; everything else — the signed state, the anonymous
/// callback, encrypted storage, the one-org-per-workspace rule, alert-rule availability — is real.
/// </summary>
[Collection("Integration")]
public class SlackInstallTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>, IDisposable
{
    private static readonly Dictionary<string, string> SlackEnvironment = new()
    {
        ["SLACK_CLIENT_ID"] = "test-client-id",
        ["SLACK_CLIENT_SECRET"] = "test-client-secret",
        ["SLACK_OAUTH_REDIRECT_URI"] = "http://localhost/api/v1/integrations/slack/oauth/callback",
        ["INTEGRATION_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    };

    private readonly FakeSlackOAuthClient _slack = new();
    private readonly WebApplicationFactory<Program> _app;

    public SlackInstallTests(PulseWebApplicationFactory factory) : base(factory)
    {
        // AppSettings reads the environment when the host's services are built; tests in the "Integration"
        // collection run one at a time, so this can't leak into a concurrent test.
        foreach (var (name, value) in SlackEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        _app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISlackOAuthClient>();
            services.AddSingleton<ISlackOAuthClient>(_slack);
        }));
    }

    public void Dispose()
    {
        foreach (var name in SlackEnvironment.Keys)
            Environment.SetEnvironmentVariable(name, null);
        _app.Dispose();
    }

    [Fact]
    public async Task A_head_connects_their_organizations_own_workspace()
    {
        var (orgId, head) = await SeedOrganizationWithHeadAsync();
        _slack.Next = new SlackOAuthResult(true, "T-ACME", "Acme Slack", "U-BOT", "xoxb-acme-secret", null);

        var callback = await CompleteInstallAsync(head);

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().EndWith("/integrations?slack=connected");

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var installation = await db.SlackInstallations.SingleAsync(s => s.OrganizationId == orgId);
            installation.TeamId.Should().Be("T-ACME");
            installation.EncryptedBotToken.Should().NotContain("xoxb", "the bot token is stored encrypted");
            scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(installation.EncryptedBotToken)
                .Should().Be("xoxb-acme-secret");
        }

        var status = (await head.GetFromJsonAsync<JsonElement>("/api/v1/integrations/slack", JsonOpts)).GetProperty("data");
        status.GetProperty("connected").GetBoolean().Should().BeTrue();
        status.GetProperty("teamName").GetString().Should().Be("Acme Slack");
    }

    [Fact]
    public async Task Once_connected_the_organization_can_route_alerts_to_Slack_and_disconnecting_stops_it()
    {
        var (orgId, head) = await SeedOrganizationWithHeadAsync();
        var team = await SeedTeamInAsync(orgId);
        object Rule() => new
        {
            name = "Blockers", metric = "blockerCount", scopeType = "team", scopeId = team, comparator = "greaterThan",
            threshold = 3, deliverInApp = true, deliverEmail = false, deliverWebhook = true, webhookUrl = "https://hooks.example.test/x",
            slackChannel = "#alerts",
        };

        (await head.PostAsJsonAsync("/api/v1/alert-rules", Rule())).IsSuccessStatusCode.Should().BeFalse("not connected yet");

        _slack.Next = new SlackOAuthResult(true, $"T-{Guid.NewGuid():N}"[..12], "Acme", "U-BOT", "xoxb-1", null);
        (await CompleteInstallAsync(head)).Headers.Location!.ToString().Should().EndWith("slack=connected");
        var created = await head.PostAsJsonAsync("/api/v1/alert-rules", Rule());
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        (await head.DeleteAsync("/api/v1/integrations/slack")).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await head.GetFromJsonAsync<JsonElement>("/api/v1/integrations/slack", JsonOpts)).GetProperty("data");
        status.GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_already_connected_to_another_organization_is_refused()
    {
        var teamId = $"T-{Guid.NewGuid():N}"[..12];
        var (_, firstHead) = await SeedOrganizationWithHeadAsync();
        var (secondOrg, secondHead) = await SeedOrganizationWithHeadAsync();
        _slack.Next = new SlackOAuthResult(true, teamId, "Shared", "U-BOT", "xoxb-first", null);
        await CompleteInstallAsync(firstHead);

        _slack.Next = new SlackOAuthResult(true, teamId, "Shared", "U-BOT", "xoxb-second", null);
        var callback = await CompleteInstallAsync(secondHead);

        callback.Headers.Location!.ToString().Should().EndWith("slack=error&reason=workspace_taken");
        using var scope = _app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<PulseDbContext>().SlackInstallations.AnyAsync(s => s.OrganizationId == secondOrg))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_forged_or_missing_state_is_refused_without_calling_Slack()
    {
        var anonymous = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var callback = await anonymous.GetAsync("/api/v1/integrations/slack/oauth/callback?code=abc&state=forged.state");

        callback.Headers.Location!.ToString().Should().EndWith("slack=error&reason=invalid_state");
        _slack.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_on_Slacks_side_is_reported_back_to_the_app()
    {
        var anonymous = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var callback = await anonymous.GetAsync("/api/v1/integrations/slack/oauth/callback?error=access_denied&state=x");

        callback.Headers.Location!.ToString().Should().EndWith("slack=error&reason=cancelled");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Gets an install URL as the head, then plays Slack: redirects to the callback with the state.</summary>
    private async Task<HttpResponseMessage> CompleteInstallAsync(HttpClient head)
    {
        var url = (await head.GetFromJsonAsync<JsonElement>("/api/v1/integrations/slack/install-url", JsonOpts))
            .GetProperty("data").GetString()!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

        var anonymous = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await anonymous.GetAsync($"/api/v1/integrations/slack/oauth/callback?code=the-code&state={Uri.EscapeDataString(state)}");
    }

    private async Task<(Guid OrgId, HttpClient Head)> SeedOrganizationWithHeadAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var head = await SeedEngineerAsync($"slackhead_{suffix}@pulse.io", Roles.HeadOfRnD);
        Guid orgId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Organization.Create($"Slack org {suffix}", $"slack-{suffix}");
            db.Organizations.Add(org);
            var engineer = await db.Engineers.SingleAsync(e => e.Id == head.Id);
            db.Entry(engineer).Property(e => e.OrganizationId).CurrentValue = org.Id;
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        var token = await LoginTokenAsync(head.Email);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (orgId, client);
    }

    private async Task<Guid> SeedTeamInAsync(Guid orgId)
    {
        var team = await SeedTeamAsync($"Slack team {Guid.NewGuid():N}"[..20]);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(team);
        db.Entry(team).Property(t => t.OrganizationId).CurrentValue = orgId;
        await db.SaveChangesAsync();
        return team.Id;
    }

    private sealed class FakeSlackOAuthClient : ISlackOAuthClient
    {
        public SlackOAuthResult Next { get; set; } = new(false, null, null, null, null, "not_set_up");
        public int Calls { get; private set; }

        public Task<SlackOAuthResult> ExchangeCodeAsync(string code, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }
}
