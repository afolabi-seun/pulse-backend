using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Security;

/// <summary>
/// Functional smoke test: confirms the demo-reset/clear endpoints still seed/wipe data end-to-end
/// after wiring RlsServiceOverride into them. This does NOT prove the RLS-bypass fix itself —
/// this suite's app connection (like local dev's) is an unrestricted Postgres superuser, so
/// FORCE-RLS policies never actually filter anything here regardless of the resolved role.
/// The real regression guard for HttpRlsContext's role resolution (including the override) is
/// Pulse.UnitTests.Security.HttpRlsContextTests, which asserts the logic directly. This class
/// gets its own isolated Testcontainers Postgres instance (a fresh PulseWebApplicationFactory
/// per class, not shared with other test classes), so wiping all data here is safe.
/// </summary>
[Collection("Integration")]
public class DemoControllerTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public DemoControllerTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Reset_seeds_data_with_no_authenticated_user()
    {
        var response = await Client.PostAsync("/api/v1/demo/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DemoSeedSummary>(JsonOpts);
        body!.Engineers.Should().BeGreaterThan(0);
        body.Teams.Should().BeGreaterThan(0);
        body.Projects.Should().BeGreaterThan(0);

        // Confirm the rows are actually queryable back out, not just reported as inserted.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        (await db.Engineers.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Clear_removes_data_with_no_authenticated_user()
    {
        await Client.PostAsync("/api/v1/demo/reset", null);

        var response = await Client.PostAsync("/api/v1/demo/clear", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        (await db.Engineers.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("/api/v1/demo/reset")]
    [InlineData("/api/v1/demo/clear")]
    public async Task The_wipe_endpoints_are_not_found_unless_explicitly_enabled(string path)
    {
        // The environment name alone must not expose them: a server wrongly left in Development, with no opt-in, has no such route.
        await SeedEngineerAsync($"demo_gate_{Guid.NewGuid():N}@cadence.io");
        var before = await EngineerCountAsync(Factory);

        using var locked = Factory.WithWebHostBuilder(b => b.UseSetting("ENABLE_DEMO_ENDPOINTS", "false"));
        using var client = locked.CreateClient();
        var response = await client.PostAsync(path, null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EngineerCountAsync(Factory)).Should().Be(before, "a refused call wipes and seeds nothing");
    }

    private static async Task<int> EngineerCountAsync(PulseWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PulseDbContext>().Engineers.CountAsync();
    }

    [Theory]
    [InlineData("/api/v1/demo/reset")]
    [InlineData("/api/v1/demo/clear")]
    public async Task The_wipe_endpoints_refuse_when_the_database_holds_another_organization(string path)
    {
        // Multi-tenant: a wipe takes every organization's data with it, so a database with a real second tenant is off limits.
        Guid secondOrgId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var second = Pulse.Domain.Organizations.Organization.Create("Second customer", $"tenant-{Guid.NewGuid():N}"[..20]);
            db.Organizations.Add(second);
            await db.SaveChangesAsync();
            secondOrgId = second.Id;
        }

        try
        {
            var before = await EngineerCountAsync(Factory);

            var response = await Client.PostAsync(path, null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await EngineerCountAsync(Factory)).Should().Be(before, "a refused call wipes and seeds nothing");
        }
        finally
        {
            // The database is shared with the class's other tests, which need to be the only tenant again.
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Organizations.Where(o => o.Id == secondOrgId).ExecuteDeleteAsync();
        }
    }
}
