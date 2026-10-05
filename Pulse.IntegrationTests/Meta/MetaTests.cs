using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Meta;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Meta;

[Collection("Integration")]
public class MetaTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public MetaTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Unauthenticated_cannot_get_meta()
    {
        var response = await Client.GetAsync("/api/v1/meta");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Any_authenticated_engineer_can_read_priority_scale()
    {
        // Priority scale is org-wide reference text, not an admin-only setting — a plain engineer
        // needs it too, to show the tooltip when picking or reading a task's priority.
        await SeedEngineerAsync("meta_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("meta_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/meta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AppMetaDto>>(JsonOpts);
        body!.Data!.PriorityScale.Should().NotBeEmpty();
        body.Data.PriorityScale.Should().Contain(e => e.Value == 1 && !string.IsNullOrEmpty(e.Label) && !string.IsNullOrEmpty(e.Criteria));
        body.Data.PointScale.Should().NotBeEmpty();
    }
}
