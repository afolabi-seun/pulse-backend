using System.Net;
using System.Net.Http.Json;
using System.Text;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Import;

[Collection("Integration")]
public class ImportTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ImportTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static MultipartFormDataContent Csv(string content, string filename = "upload.csv")
    {
        var form = new MultipartFormDataContent();
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content)))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv") }
        }, "file", filename);
        return form;
    }

    // ── access control — all import endpoints ─────────────────────────────────

    [Theory]
    [InlineData("/api/v1/import/projects")]
    [InlineData("/api/v1/import/tasks")]
    [InlineData("/api/v1/import/backlog")]
    public async Task Engineer_cannot_access_any_import_endpoint(string url)
    {
        await SeedEngineerAsync($"imp_eng_{url.GetHashCode()}@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync($"imp_eng_{url.GetHashCode()}@pulse.io");

        var response = await client.PostAsync(url, Csv("header\r\nrow"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/v1/import/template/projects")]
    [InlineData("/api/v1/import/template/tasks")]
    [InlineData("/api/v1/import/template/backlog")]
    public async Task Templates_are_accessible_to_pm_and_above(string url)
    {
        await SeedEngineerAsync($"tmpl_pm_{url.GetHashCode()}@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync($"tmpl_pm_{url.GetHashCode()}@pulse.io");

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
    }

    [Theory]
    [InlineData("/api/v1/import/template/projects")]
    [InlineData("/api/v1/import/template/tasks")]
    [InlineData("/api/v1/import/template/backlog")]
    public async Task Templates_are_denied_to_engineers(string url)
    {
        await SeedEngineerAsync($"tmpl_eng_{url.GetHashCode()}@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync($"tmpl_eng_{url.GetHashCode()}@pulse.io");

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── projects CSV import ───────────────────────────────────────────────────

    [Fact]
    public async Task Import_projects_creates_new_projects()
    {
        await SeedEngineerAsync("imp_proj_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_proj_pm@pulse.io");

        const string csv = "name,description\r\nAlpha Initiative,First project\r\nBeta Initiative,Second project\r\n";

        var response = await client.PostAsync("/api/v1/import/projects", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(2);
        result.Data.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_projects_returns_400_when_no_file_uploaded()
    {
        await SeedEngineerAsync("imp_proj_nofile@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_proj_nofile@pulse.io");

        var response = await client.PostAsync("/api/v1/import/projects", new MultipartFormDataContent());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_projects_skips_rows_with_empty_name()
    {
        await SeedEngineerAsync("imp_proj_skip@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_proj_skip@pulse.io");

        const string csv = "name,description\r\nValidProject,ok\r\n,no name here\r\n";

        var response = await client.PostAsync("/api/v1/import/projects", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(1);
    }

    [Fact]
    public async Task Import_projects_returns_400_for_missing_name_column_in_header()
    {
        await SeedEngineerAsync("imp_proj_hdr@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_proj_hdr@pulse.io");

        const string csv = "description\r\nSome description\r\n";

        var response = await client.PostAsync("/api/v1/import/projects", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Projects_template_contains_expected_columns()
    {
        await SeedEngineerAsync("tmpl_proj_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("tmpl_proj_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/import/template/projects");
        var csv = await response.Content.ReadAsStringAsync();

        csv.Should().Contain("name").And.Contain("description");
    }

    // ── users CSV import ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_users_allows_a_blank_team_for_the_executive_role()
    {
        // Executive is the one role explicitly designed to have no team — a real PMO hit
        // "Team is required" trying to import one before this was fixed.
        await SeedEngineerAsync("imp_users_exec_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("imp_users_exec_pmo@pulse.io");

        const string csv = "name,email,role,team\r\nExec Import,exec_import@pulse.io,executive,\r\n";

        var response = await client.PostAsync("/api/v1/import/users", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(1);
        result.Data.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_users_still_requires_team_for_non_executive_roles()
    {
        await SeedEngineerAsync("imp_users_noteam_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("imp_users_noteam_pmo@pulse.io");

        const string csv = "name,email,role,team\r\nNo Team Engineer,no_team_eng@pulse.io,engineer,\r\n";

        var response = await client.PostAsync("/api/v1/import/users", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("Team is required"));
    }

    // ── tasks CSV import ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_tasks_returns_400_when_no_file_uploaded()
    {
        await SeedEngineerAsync("imp_tasks_nofile@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_nofile@pulse.io");

        var response = await client.PostAsync("/api/v1/import/tasks", new MultipartFormDataContent());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_tasks_returns_400_for_missing_required_columns_in_header()
    {
        await SeedEngineerAsync("imp_tasks_hdr@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_hdr@pulse.io");

        const string csv = "title,description\r\nSome task,no project\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_tasks_fails_rows_for_unknown_project()
    {
        await SeedEngineerAsync("imp_tasks_proj@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_proj@pulse.io");

        const string csv =
            "project_name,title,description,points,due_date,type,assignee_email\r\n" +
            "NonExistentProject,Task A,,3,2026-12-31,Feature,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_tasks_reads_day_first_dates_and_names_one_it_cannot_read()
    {
        await SeedEngineerAsync("imp_tasks_dates@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_dates@pulse.io");
        var project = await SeedProjectAsync("Day first dates");

        // 16/10 only makes sense day-first (there is no month 16); 09/10 is the one a month-first reading would silently turn into 10 September.
        const string csv =
            "project_name,title,points,due_date,priority,type,assignee_email\r\n" +
            "Day first dates,Sixteenth,3,16/10/2026,3,Bug,\r\n" +
            "Day first dates,Ninth,3,09/10/2026,2,Bug,\r\n" +
            "Day first dates,Impossible,3,31/02/2026,2,Bug,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>())!.Data!;
        result.Created.Should().Be(2);
        result.Failures.Should().ContainSingle(f => f.Error.Contains("31/02/2026"));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
        var due = db.Tasks.Where(t => t.ProjectId == project.Id).ToDictionary(t => t.Title, t => t.DueDate);
        due["Sixteenth"].Should().Be(new DateOnly(2026, 10, 16));
        due["Ninth"].Should().Be(new DateOnly(2026, 10, 9));
    }

    [Fact]
    public async Task Import_tasks_handles_quoted_description_spanning_multiple_lines()
    {
        await SeedEngineerAsync("imp_tasks_multiline@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_multiline@pulse.io");
        await SeedProjectAsync("Multiline Project");

        const string csv =
            "project_name,title,description,points,due_date,type,assignee_email\r\n" +
            "Multiline Project,Task A,\"Line one, with a comma\nLine two\nLine three\",5,2026-12-31,Feature,\r\n" +
            "Multiline Project,Task B,Second task,3,2026-12-31,Bug,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Failures.Should().BeEmpty();
        result.Data.Created.Should().Be(2);
    }

    [Fact]
    public async Task Import_tasks_sets_acceptance_criteria_from_the_csv_column()
    {
        await SeedEngineerAsync("imp_tasks_ac@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_ac@pulse.io");
        await SeedProjectAsync("AC Import Project");

        const string csv =
            "project_name,title,description,acceptance_criteria,points,due_date,type,assignee_email\r\n" +
            "AC Import Project,Task with AC,,\"Given X, when Y, then Z.\",3,2026-12-31,Feature,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(1);

        var listResponse = await client.GetAsync("/api/v1/tasks?title=" + Uri.EscapeDataString("Task with AC"));
        var listResult = await listResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>();
        listResult!.Data!.Items.Should().ContainSingle()
            .Which.AcceptanceCriteria.Should().Be("Given X, when Y, then Z.");
    }

    [Fact]
    public async Task Import_tasks_sets_priority_from_the_csv_column()
    {
        await SeedEngineerAsync("imp_tasks_priority@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_priority@pulse.io");
        await SeedProjectAsync("Priority Import Project");

        const string csv =
            "project_name,title,points,due_date,priority,type,assignee_email\r\n" +
            "Priority Import Project,Task with priority,3,2026-12-31,2,Feature,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(1);

        var listResponse = await client.GetAsync("/api/v1/tasks?title=" + Uri.EscapeDataString("Task with priority"));
        var listResult = await listResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>();
        listResult!.Data!.Items.Should().ContainSingle().Which.Priority.Should().Be(2);
    }

    [Fact]
    public async Task Import_tasks_rejects_a_row_with_an_out_of_range_priority()
    {
        await SeedEngineerAsync("imp_tasks_bad_priority@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("imp_tasks_bad_priority@pulse.io");
        await SeedProjectAsync("Bad Priority Import Project");

        const string csv =
            "project_name,title,points,due_date,priority,type,assignee_email\r\n" +
            "Bad Priority Import Project,Bad priority task,3,2026-12-31,9,Feature,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("priority"));
    }

    [Fact]
    public async Task Import_tasks_rejects_a_row_targeting_a_project_the_actor_cannot_access()
    {
        // A department head importing into a project outside their department (and not followed)
        // must now be rejected, the same as a manual create would be — the importer previously had
        // no access check of its own at all.
        var head = await SeedEngineerAsync("imp_tasks_no_access_head@pulse.io", Roles.HeadOfRnD);
        var headTeam = await SeedTeamAsync("Import No-Access Head Team", head.Id, department: "R&D");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var headEng = await db.Engineers.FindAsync(head.Id);
            headEng!.AssignToTeam(headTeam.Id);
            await db.SaveChangesAsync();
        }
        var otherTeam = await SeedTeamAsync("Import No-Access Other Team", department: "Design");
        var project = await SeedProjectAsync("No Access Import Project", ownerTeamId: otherTeam.Id);
        var client = await AuthenticatedClientAsync("imp_tasks_no_access_head@pulse.io");

        var csv =
            "project_name,title,points,due_date,type,assignee_email\r\n" +
            $"{project.Name},Out of reach task,0,,Feature,\r\n";

        var response = await client.PostAsync("/api/v1/import/tasks", Csv(csv));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ImportResult>>();
        result!.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("access"));
    }

    [Fact]
    public async Task Tasks_template_contains_expected_columns()
    {
        await SeedEngineerAsync("tmpl_tasks_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("tmpl_tasks_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/import/template/tasks");
        var csv = await response.Content.ReadAsStringAsync();

        csv.Should().Contain("project_name")
           .And.Contain("title")
           .And.Contain("due_date");
    }

    // ── unauthenticated access ────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/v1/import/projects")]
    [InlineData("/api/v1/import/tasks")]
    [InlineData("/api/v1/import/backlog")]
    [InlineData("/api/v1/import/template/projects")]
    [InlineData("/api/v1/import/template/tasks")]
    [InlineData("/api/v1/import/template/backlog")]
    public async Task Unauthenticated_request_returns_401(string url)
    {
        var client = Factory.CreateClient();

        var response = url.Contains("template")
            ? await client.GetAsync(url)
            : await client.PostAsync(url, Csv("header\r\nrow"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
