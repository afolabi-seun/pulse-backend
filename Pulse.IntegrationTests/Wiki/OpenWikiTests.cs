using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Wiki;

/// <summary>A wiki page is readable by every signed-in user unless its author restricted it to the project's
/// members. Writing, and a page's revision history, stay with the project.</summary>
[Collection("Integration")]
public class OpenWikiTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public OpenWikiTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<(Project Project, WikiPage Open, WikiPage Restricted)> SeedProjectWithPagesAsync(string tag)
    {
        var team = await SeedTeamAsync($"Open wiki team {tag}");
        var project = await SeedProjectAsync($"Open wiki project {tag}", team.Id);
        var author = await SeedEngineerAsync($"ow_author_{tag}@pulse.io", Roles.ProjectManager);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var open = WikiPage.Create(project.Id, $"Open page {tag}", "For everyone", author.Id);
        var restricted = WikiPage.Create(project.Id, $"Restricted page {tag}", "Members only", author.Id, restrictedToMembers: true);
        db.WikiPages.AddRange(open, restricted);
        await db.SaveChangesAsync();
        return (project, open, restricted);
    }

    [Fact]
    public async Task A_non_member_can_read_an_open_page_and_find_it_in_the_index_but_not_a_restricted_one()
    {
        var (project, open, restricted) = await SeedProjectWithPagesAsync("nm");
        await SeedEngineerAsync("ow_outsider_nm@pulse.io", Roles.Engineer);
        var outsider = await AuthenticatedClientAsync("ow_outsider_nm@pulse.io");

        (await outsider.GetAsync($"/api/v1/projects/{project.Id}/wiki/{open.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await outsider.GetAsync($"/api/v1/projects/{project.Id}/wiki/{restricted.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.GetAsync($"/api/v1/projects/{project.Id}/wiki/{open.Id}/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await outsider.GetAsync($"/api/v1/projects/{project.Id}/wiki/{restricted.Id}/pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var index = (await (await outsider.GetAsync("/api/v1/wiki")).Content
            .ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiIndexEntryDto>>>(JsonOpts))!.Data!.Items;
        index.Select(e => e.PageId).Should().Contain(open.Id).And.NotContain(restricted.Id);
        index.Single(e => e.PageId == open.Id).ProjectName.Should().Be(project.Name);
    }

    [Fact]
    public async Task A_project_member_still_sees_the_restricted_page()
    {
        var (project, _, restricted) = await SeedProjectWithPagesAsync("mem");
        var member = await SeedEngineerAsync("ow_member_mem@pulse.io", Roles.Engineer);
        await SeedProjectMemberAsync(project.Id, member.Id);
        var client = await AuthenticatedClientAsync("ow_member_mem@pulse.io");

        (await client.GetAsync($"/api/v1/projects/{project.Id}/wiki/{restricted.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var index = (await (await client.GetAsync("/api/v1/wiki")).Content
            .ReadFromJsonAsync<ApiResponse<PagedResultDto<WikiIndexEntryDto>>>(JsonOpts))!.Data!.Items;
        index.Select(e => e.PageId).Should().Contain(restricted.Id);
    }

    [Fact]
    public async Task A_non_member_cannot_see_a_pages_revision_history_or_write_to_it()
    {
        var (project, open, _) = await SeedProjectWithPagesAsync("hist");
        await SeedEngineerAsync("ow_outsider_hist@pulse.io", Roles.Engineer);
        var outsider = await AuthenticatedClientAsync("ow_outsider_hist@pulse.io");

        (await outsider.GetAsync($"/api/v1/projects/{project.Id}/wiki/{open.Id}/revisions")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await outsider.PutAsJsonAsync($"/api/v1/projects/{project.Id}/wiki/{open.Id}", new { title = "Edited", content = "x" }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_author_can_restrict_a_page_and_editing_without_the_field_does_not_reopen_it()
    {
        var (project, open, _) = await SeedProjectWithPagesAsync("flip");
        var pm = await SeedEngineerAsync("ow_pm_flip@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("ow_pm_flip@pulse.io");
        var url = $"/api/v1/projects/{project.Id}/wiki/{open.Id}";

        var restrict = await client.PutAsJsonAsync(url, new { title = "Now closed", content = "x", restrictedToMembers = true });
        restrict.StatusCode.Should().Be(HttpStatusCode.OK);
        (await restrict.Content.ReadFromJsonAsync<ApiResponse<WikiPageDto>>(JsonOpts))!.Data!.RestrictedToMembers.Should().BeTrue();

        var edit = await client.PutAsJsonAsync(url, new { title = "Edited again", content = "y" });
        (await edit.Content.ReadFromJsonAsync<ApiResponse<WikiPageDto>>(JsonOpts))!.Data!.RestrictedToMembers.Should().BeTrue();
        pm.Id.Should().NotBeEmpty();
    }
}
