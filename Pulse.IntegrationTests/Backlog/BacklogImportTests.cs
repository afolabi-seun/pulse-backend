using System.Net;
using System.Net.Http.Json;
using System.Text;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Backlog;

[Collection("Integration")]
public class BacklogImportTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public BacklogImportTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_import_backlog()
    {
        await SeedEngineerAsync("bl_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("bl_eng@pulse.io");
        var content = BuildCsvContent("project_name,epic_name,title\r\nCIB,Auth,Login");

        var response = await client.PostAsync("/api/v1/import/backlog", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Template_endpoint_returns_csv_file()
    {
        await SeedEngineerAsync("bl_tmpl_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_tmpl_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/import/template/backlog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await response.Content.ReadAsStringAsync();
        csv.Should().Contain("project_name").And.Contain("epic_name").And.Contain("title");
    }

    // ── happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_creates_project_epic_and_tasks_when_none_exist()
    {
        await SeedEngineerAsync("bl_happy_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_happy_pm@pulse.io");

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            NewBank,EPIC-001,Authentication,,US-001,Admin Login,Isolated admin access.,Admin sees dedicated login.,must_have,Feature,,MVP1,
            NewBank,EPIC-001,Authentication,,US-002,Password Reset,Reduced support overhead.,User can reset via email.,must_have,Feature,,MVP1,
            NewBank,EPIC-002,Payments,Own Account Transfer,US-003,Transfer funds,Convenient self-service.,User selects source and destination.,must_have,Feature,5,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(3);
        result.Data.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_reuses_existing_project_and_creates_new_epic()
    {
        await SeedEngineerAsync("bl_reuse_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_reuse_pm@pulse.io");
        var team = await SeedTeamAsync("ExistingProject Team");

        // Create a project first via the projects endpoint
        await client.PostAsJsonAsync("/api/v1/projects", new { name = "ExistingProject", ownerTeamId = team.Id });

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            ExistingProject,,New Epic,,US-001,First story,,,must_have,Feature,3,,
            ExistingProject,,New Epic,,US-002,Second story,,,should_have,Feature,2,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(2);
        result.Data.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_allows_zero_story_points()
    {
        await SeedEngineerAsync("bl_zero_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_zero_pm@pulse.io");

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            PointsProject,,Backlog Epic,,,Unpointed story,,,,Feature,,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();

        result!.Data!.Created.Should().Be(1, because: "blank story_points should default to 0, not fail");
    }

    [Fact]
    public async Task Import_fails_rows_missing_title()
    {
        await SeedEngineerAsync("bl_fail_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_fail_pm@pulse.io");

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            BadProject,,Auth,,,,,,,Feature,,,
            BadProject,,Auth,,US-001,Valid story,,,must_have,Feature,,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();

        result!.Data!.Created.Should().Be(1);
        result.Data.Failures.Should().HaveCount(1);
        result.Data.Failures[0].Row.Should().Be(2);
    }

    [Fact]
    public async Task Import_handles_short_row_missing_trailing_project_name_column()
    {
        // Reordered header puts project_name last; a row that omits trailing
        // empty cells (common when exported from a spreadsheet) has no column
        // at that index at all, not just an empty one.
        await SeedEngineerAsync("bl_short_row_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_short_row_pm@pulse.io");

        var csv = """
            title,epic_name,project_name
            Some story,Auth
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_reuses_same_epic_for_multiple_stories()
    {
        await SeedEngineerAsync("bl_epic_reuse_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_epic_reuse_pm@pulse.io");

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            EpicReuseProject,,Shared Epic,,US-001,Story One,,,must_have,Feature,,,
            EpicReuseProject,,Shared Epic,,US-002,Story Two,,,must_have,Feature,,,
            EpicReuseProject,,Shared Epic,,US-003,Story Three,,,must_have,Feature,,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();

        result!.Data!.Created.Should().Be(3);
        result.Data.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_maps_the_MoSCoW_priority_column_onto_the_real_priority_field()
    {
        await SeedEngineerAsync("bl_priority_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("bl_priority_pm@pulse.io");

        var csv = """
            project_name,epic_ref,epic_name,feature,story_ref,title,description,acceptance_criteria,priority,type,story_points,phase,notes
            PriorityProject,,Priority Epic,,US-100,Must have story,Some real description.,,must_have,Feature,3,,
            """;

        var response = await client.PostAsync("/api/v1/import/backlog", BuildCsvContent(csv));
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(1);

        var project = (await (await client.GetAsync("/api/v1/projects")).Content
            .ReadFromJsonAsync<ApiResponse<IReadOnlyList<Pulse.Application.Projects.ProjectDto>>>())!
            .Data!.Single(p => p.Name == "PriorityProject");
        var tasksResp = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}");
        var task = (await tasksResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<Pulse.Application.Tasks.TaskDto>>>())!
            .Data!.Items.Single();

        task.Priority.Should().Be(5, "must_have should map to the highest priority level");
        task.Description.Should().NotContain("Priority:", "priority now has a real field instead of being dumped into the description");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static MultipartFormDataContent BuildCsvContent(string csv)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(csv)))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv") }
        }, "file", "backlog.csv");
        return content;
    }
}
