using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Wiki;

/// <summary>Wiki pages have a type (Development, Architecture, Product, PMO, Functional, General) so documents can be grouped by what they are.</summary>
[Collection("Integration")]
public class WikiCategoryTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public WikiCategoryTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<(HttpClient Client, Guid ProjectId)> SetupAsync(string tag)
    {
        await SeedEngineerAsync($"wc_pm_{tag}@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync($"Wiki types {tag}");
        return (await AuthenticatedClientAsync($"wc_pm_{tag}@pulse.io"), project.Id);
    }

    private static async Task<WikiPageDto> Read(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<ApiResponse<WikiPageDto>>(JsonOpts))!.Data!;

    [Fact]
    public async Task A_page_created_with_no_type_is_General()
    {
        var (client, projectId) = await SetupAsync("default");

        var created = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/wiki", new { title = "Untyped", content = "# x" });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Read(created)).Category.Should().Be("general");
    }

    [Fact]
    public async Task A_page_keeps_the_type_it_was_created_with_and_can_be_retyped()
    {
        var (client, projectId) = await SetupAsync("retype");
        var created = await Read(await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/wiki",
            new { title = "Service map", content = "# x", category = "Architecture" }));
        created.Category.Should().Be("architecture");

        (await Read(await client.GetAsync($"/api/v1/projects/{projectId}/wiki/{created.Id}"))).Category.Should().Be("architecture");

        var updated = await Read(await client.PutAsJsonAsync($"/api/v1/projects/{projectId}/wiki/{created.Id}",
            new { title = "Service map", content = "# y", category = "development" }));
        updated.Category.Should().Be("development");

        // An edit that says nothing about the type must not reset it.
        var edited = await Read(await client.PutAsJsonAsync($"/api/v1/projects/{projectId}/wiki/{created.Id}",
            new { title = "Service map v3", content = "# z" }));
        edited.Category.Should().Be("development");
    }

    [Fact]
    public async Task An_unknown_type_is_refused_on_create_and_on_update()
    {
        var (client, projectId) = await SetupAsync("bad");

        var create = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/wiki", new { title = "Bad", content = "x", category = "marketing" });
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await create.Content.ReadAsStringAsync()).Should().Contain("marketing");

        var ok = await Read(await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/wiki", new { title = "Fine", content = "x" }));
        var update = await client.PutAsJsonAsync($"/api/v1/projects/{projectId}/wiki/{ok.Id}", new { title = "Fine", content = "x", category = "nope" });
        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_type_shows_in_the_project_page_list_and_in_the_cross_project_index()
    {
        var (client, projectId) = await SetupAsync("lists");
        var page = await Read(await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/wiki",
            new { title = "Roadmap", content = "x", category = "product" }));

        var list = (await (await client.GetAsync($"/api/v1/projects/{projectId}/wiki"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiPageSummaryDto>>>(JsonOpts))!.Data!.Items;
        list.Single(p => p.Id == page.Id).Category.Should().Be("product");

        var index = (await (await client.GetAsync("/api/v1/wiki?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiIndexEntryDto>>>(JsonOpts))!.Data!.Items;
        index.Single(e => e.PageId == page.Id).Category.Should().Be("product");
    }
}
