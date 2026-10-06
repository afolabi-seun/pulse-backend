using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Attributes;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 2a: the operator creates organizations (with their first Head) behind
/// OPERATOR_API_KEY; and email stays unique across organizations even though users can only see their own.
/// </summary>
[Collection("Integration")]
public class OperatorTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private const string OperatorKey = "test-operator-key-0123456789abcdefghij";

    public OperatorTests(PulseWebApplicationFactory factory) : base(factory) { }

    private HttpClient OperatorClient(string? key = OperatorKey)
    {
        var client = Factory.WithWebHostBuilder(b => b.UseSetting(OperatorAuthAttribute.SettingName, OperatorKey)).CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add(OperatorAuthAttribute.HeaderName, key);
        return client;
    }

    [Fact]
    public async Task Operator_endpoints_do_not_exist_when_no_key_is_configured()
    {
        var client = Factory.CreateClient(); // the default factory sets no OPERATOR_API_KEY
        client.DefaultRequestHeaders.Add(OperatorAuthAttribute.HeaderName, OperatorKey);

        (await client.GetAsync("/api/v1/operator/organizations")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-0123456789abcdefghijklmnop")]
    public async Task A_missing_or_wrong_operator_key_is_rejected(string? key)
    {
        var response = await OperatorClient(key).PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = "Nope", slug = $"nope-{Guid.NewGuid():N}"[..12], adminName = "X", adminEmail = $"nope_{Guid.NewGuid():N}@pulse.io" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creating_an_organization_invites_its_first_head_into_it()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var adminEmail = $"head_{suffix}@acme.test";

        var response = await OperatorClient().PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = "Acme Corp", slug = $"acme-{suffix}", adminName = "Ada Head", adminEmail, billingEmail = "billing@acme.test" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("data");
        var orgId = data.GetProperty("organization").GetProperty("id").GetGuid();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var org = await db.Organizations.SingleAsync(o => o.Id == orgId);
        org.Slug.Should().Be($"acme-{suffix}");
        var admin = await db.Engineers.SingleAsync(e => e.Email == adminEmail);
        admin.OrganizationId.Should().Be(orgId);
        admin.Role.Should().Be(Roles.HeadOfRnD);
        admin.PasswordResetToken.Should().NotBeNull("the Head activates their account from the emailed link");
    }

    [Fact]
    public async Task Organization_slugs_and_admin_emails_must_be_unused()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var client = OperatorClient();
        (await client.PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = "First", slug = $"taken-{suffix}", adminName = "A", adminEmail = $"first_{suffix}@pulse.io" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = "Second", slug = $"taken-{suffix}", adminName = "B", adminEmail = $"second_{suffix}@pulse.io" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "the slug is taken");

        var existing = await SeedEngineerAsync($"existing_{suffix}@pulse.io");
        (await client.PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = "Third", slug = $"third-{suffix}", adminName = "C", adminEmail = existing.Email }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "one account per email, across all organizations");
    }

    [Fact]
    public async Task Inviting_an_email_that_belongs_to_another_organization_is_a_conflict_not_an_error()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgAEngineer = await SeedEngineerAsync($"taken_{suffix}@pulse.io");
        var orgBHead = await CreateOrganizationAndActivateHeadAsync(suffix);
        var orgBTeam = await SeedTeamInOrganizationAsync($"org-{suffix}", $"Org B team {suffix}");

        var response = await orgBHead.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clash", email = orgAEngineer.Email, role = Roles.Engineer, baselinePoints = 20, baselineCycleDays = 14,
            teamId = orgBTeam,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_user_sees_their_own_organization()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgBHead = await CreateOrganizationAndActivateHeadAsync(suffix);

        var body = await orgBHead.GetFromJsonAsync<JsonElement>("/api/v1/organization", JsonOpts);

        body.GetProperty("data").GetProperty("name").GetString().Should().Be($"Org {suffix}");
        body.GetProperty("data").GetProperty("slug").GetString().Should().Be($"org-{suffix}");
    }

    [Fact]
    public async Task Another_organization_cannot_route_alerts_to_the_deployments_Slack_yet()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgBHead = await CreateOrganizationAndActivateHeadAsync(suffix);
        var orgBTeam = await SeedTeamInOrganizationAsync($"org-{suffix}", $"Alerting team {suffix}");

        object Rule(string? slackChannel) => new
        {
            name = "Blockers", metric = "blockerCount", scopeType = "team", scopeId = orgBTeam,
            comparator = "greaterThan", threshold = 3, deliverInApp = true, deliverEmail = false,
            deliverWebhook = slackChannel is not null, slackChannel,
        };

        var toSlack = await orgBHead.PostAsJsonAsync("/api/v1/alert-rules", Rule("#alerts"));
        toSlack.IsSuccessStatusCode.Should().BeFalse("org B's alerts would land in the default org's Slack workspace");
        (await toSlack.Content.ReadAsStringAsync()).Should().Contain("Connect your organization's Slack workspace");

        (await orgBHead.PostAsJsonAsync("/api/v1/alert-rules", Rule(null)))
            .StatusCode.Should().Be(HttpStatusCode.Created, "in-app delivery works for every organization");
    }

    private async Task<Guid> SeedTeamInOrganizationAsync(string orgSlug, string teamName)
    {
        var team = await SeedTeamAsync(teamName);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var orgId = (await db.Organizations.SingleAsync(o => o.Slug == orgSlug)).Id;
        db.Attach(team);
        db.Entry(team).Property(t => t.OrganizationId).CurrentValue = orgId;
        await db.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>Creates an org through the operator API, then gives its Head a known password (standing in
    /// for following the activation link) and returns a client logged in as them.</summary>
    private async Task<HttpClient> CreateOrganizationAndActivateHeadAsync(string suffix)
    {
        var email = $"orghead_{suffix}@pulse.io";
        (await OperatorClient().PostAsJsonAsync("/api/v1/operator/organizations",
            new { name = $"Org {suffix}", slug = $"org-{suffix}", adminName = "Org Head", adminEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();
            var head = await db.Engineers.SingleAsync(e => e.Email == email);
            head.SetPasswordHash(hasher.Hash("Str0ng!Pass12"));
            await db.SaveChangesAsync();
        }

        return await AuthenticatedClientAsync(email);
    }
}
