using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>A wiki page's type is just a column, but pages are scoped to an organization through their project: another organization must not see a
/// typed page in its index, open it, or change its type.</summary>
[Collection("Integration")]
public class WikiCategoryIsolationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public WikiCategoryIsolationTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Another_organization_cannot_see_open_or_retype_a_typed_page()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Guid orgBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var orgB = Organization.Create("Wiki types org B", $"wiki-b-{suffix}");
            db.Organizations.Add(orgB);
            await db.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        await SeedEngineerAsync($"wikiiso_a_{suffix}@pulse.io", Roles.HeadOfPmo);
        var pmoB = await SeedEngineerAsync($"wikiiso_b_{suffix}@pulse.io", Roles.HeadOfPmo);
        await MoveToOrganizationAsync(pmoB, orgBId);
        var project = await SeedProjectAsync($"Wiki isolation {suffix}");
        var a = await AuthenticatedClientAsync($"wikiiso_a_{suffix}@pulse.io");
        var b = await AuthenticatedClientAsync($"wikiiso_b_{suffix}@pulse.io");

        var created = (await (await a.PostAsJsonAsync($"/api/v1/projects/{project.Id}/wiki",
            new { title = $"Org A design {suffix}", content = "x", category = "architecture" }))
            .Content.ReadFromJsonAsync<ApiResponse<WikiPageDto>>(JsonOpts))!.Data!;
        created.Category.Should().Be("architecture");

        // Control: the page's own organization sees it, with its type, in the cross-project index.
        var indexA = (await (await a.GetAsync("/api/v1/wiki?limit=100")).Content
            .ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiIndexEntryDto>>>(JsonOpts))!.Data!.Items;
        indexA.Single(e => e.PageId == created.Id).Category.Should().Be("architecture");

        // Another organization: not in its index, not openable, not changeable.
        var indexB = (await (await b.GetAsync("/api/v1/wiki?limit=100")).Content
            .ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiIndexEntryDto>>>(JsonOpts))!.Data!.Items;
        indexB.Should().NotContain(e => e.PageId == created.Id);

        (await b.GetAsync($"/api/v1/projects/{project.Id}/wiki/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var retype = await b.PutAsJsonAsync($"/api/v1/projects/{project.Id}/wiki/{created.Id}",
            new { title = "hijack", content = "x", category = "pmo" });
        retype.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);

        var still = (await (await a.GetAsync($"/api/v1/projects/{project.Id}/wiki/{created.Id}")).Content
            .ReadFromJsonAsync<ApiResponse<WikiPageDto>>(JsonOpts))!.Data!;
        still.Category.Should().Be("architecture");
        still.Title.Should().Be($"Org A design {suffix}");
    }
}
