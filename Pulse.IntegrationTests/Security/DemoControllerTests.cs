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
}
