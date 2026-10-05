using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Service-to-service tokens stay org-agnostic, as the multi-tenancy design specifies. This is the
/// end-to-end acceptance check: a real service token reaches a [ServiceAuth] endpoint for an engineer in a
/// non-default organization. The scoping itself — that a service token is not "a user with no org", which
/// the org filters and RLS would show nothing — is pinned by CurrentUserServiceTests and HttpRlsContextTests;
/// it only bites here under the production runtime role's RLS, which these superuser tests don't exercise.
/// </summary>
[Collection("Integration")]
public class ServiceTokenTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ServiceTokenTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task A_service_token_can_notify_an_engineer_in_any_organization()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var engineer = await SeedEngineerAsync($"svc_target_{suffix}@pulse.io");
        await MoveToOtherOrganizationAsync(engineer.Id, suffix);

        string token;
        using (var scope = Factory.Services.CreateScope())
            token = scope.ServiceProvider.GetRequiredService<IJwtService>().GenerateServiceToken("integration-test");
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/notifications",
            new { userId = engineer.Id, kind = "checkin_reminder", payload = "{}", channel = "in_app" });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task MoveToOtherOrganizationAsync(Guid engineerId, string suffix)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var org = Organization.Create("Service token org", $"svc-{suffix}");
        db.Organizations.Add(org);
        var engineer = db.Engineers.Single(e => e.Id == engineerId);
        db.Entry(engineer).Property(e => e.OrganizationId).CurrentValue = org.Id;
        await db.SaveChangesAsync();
    }
}
