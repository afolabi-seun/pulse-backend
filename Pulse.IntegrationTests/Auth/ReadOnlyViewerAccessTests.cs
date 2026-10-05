using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Reports;
using Pulse.Application.Sprints;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Auth;

/// <summary>Executive, HR and Accountant are org-wide, read-only, no-team roles: they can read
/// Projects, Sprints, Wiki, epics and task detail across the org (Roles.IsOrgReadOnlyViewer), and can
/// never write through that access. Time logging and task creation are separate, narrower grants
/// covered at the bottom.</summary>
[Collection("Integration")]
public class ReadOnlyViewerAccessTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ReadOnlyViewerAccessTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string EmailFor(string role, string tag) => $"rov_{role}_{tag}@pulse.io";

    private async Task<HttpClient> ViewerAsync(string role, string tag)
    {
        await SeedEngineerAsync(EmailFor(role, tag), role);
        return await AuthenticatedClientAsync(EmailFor(role, tag));
    }

    // ── read access, org-wide ──────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_can_read_sprints_in_a_team_they_do_not_belong_to(string role)
    {
        var client = await ViewerAsync(role, "sprint");
        var team = await SeedTeamAsync($"ROV sprint team {role}");
        var sprint = await SeedSprintAsync(team.Id, $"ROV sprint {role}");

        var list = await client.GetAsync("/api/v1/sprints");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<SprintDto>>>(JsonOpts);
        body!.Data.Should().Contain(s => s.Id == sprint.Id);

        (await client.GetAsync($"/api/v1/sprints/{sprint.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/sprints/{sprint.Id}/velocity")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/sprints/{sprint.Id}/burndown")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/sprints/{sprint.Id}/retrospective")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_can_read_wiki_pages_in_a_project_they_are_not_a_member_of(string role)
    {
        var client = await ViewerAsync(role, "wiki");
        var team = await SeedTeamAsync($"ROV wiki team {role}");
        var project = await SeedProjectAsync($"ROV wiki project {role}", team.Id);
        var author = await SeedEngineerAsync(EmailFor(role, "wiki_author"), Roles.ProjectManager);

        Guid pageId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var page = WikiPage.Create(project.Id, "Runbook", "Some content", author.Id);
            db.WikiPages.Add(page);
            await db.SaveChangesAsync();
            pageId = page.Id;
        }

        (await client.GetAsync("/api/v1/wiki")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/projects/{project.Id}/wiki")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/projects/{project.Id}/wiki/{pageId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/projects/{project.Id}/wiki/{pageId}/revisions")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_can_read_a_project_its_activity_epics_members_and_a_task(string role)
    {
        var client = await ViewerAsync(role, "project");
        var team = await SeedTeamAsync($"ROV project team {role}");
        var project = await SeedProjectAsync($"ROV project {role}", team.Id);
        var task = await SeedTaskAsync($"ROV task {role}", project.Id);

        (await client.GetAsync($"/api/v1/projects/{project.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/projects/{project.Id}/activity")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/projects/{project.Id}/members")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/epics?projectId={project.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/tasks/{task.Id}/comments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/tasks/{task.Id}/subtasks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/time-entries/task-summary/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/tasks/{task.Id}/mention-candidates")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_can_load_the_lists_the_projects_sprints_and_standup_pages_depend_on(string role)
    {
        var client = await ViewerAsync(role, "lists");

        (await client.GetAsync("/api/v1/teams")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/engineers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/check-ins/standup")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/reports/pmo")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_can_open_the_leadership_report_and_its_pdf_with_the_engineer_table(string role)
    {
        var client = await ViewerAsync(role, "leadership");
        var team = await SeedTeamAsync($"ROV leadership team {role}");
        var engineer = await SeedEngineerAsync(EmailFor(role, "leadership_eng"), Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts);
        body!.Data!.Engineers.Should().Contain(e => e.EngineerId == engineer.Id);
        (await client.GetAsync("/api/v1/reports/leadership/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── never write through the read access ────────────────────────────────────────

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Viewer_cannot_create_a_sprint_wiki_page_or_delete_a_project(string role)
    {
        var client = await ViewerAsync(role, "writes");
        var team = await SeedTeamAsync($"ROV writes team {role}");
        var project = await SeedProjectAsync($"ROV writes project {role}", team.Id);

        var sprint = await client.PostAsJsonAsync("/api/v1/sprints", new
        {
            name = "Nope", teamId = team.Id, projectId = project.Id,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd"),
        });
        var wiki = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/wiki", new { title = "Nope", content = "Nope" });
        var delete = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        sprint.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        wiki.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── time logging: HR and Accountant yes, Executive no ─────────────────────────────

    [Theory]
    [InlineData(Roles.HR, HttpStatusCode.OK)]
    [InlineData(Roles.Accountant, HttpStatusCode.OK)]
    [InlineData(Roles.Executive, HttpStatusCode.Forbidden)]
    public async Task Time_logging_is_open_to_hr_and_accountant_but_not_executive(string role, HttpStatusCode expected)
    {
        var client = await ViewerAsync(role, "time");

        var response = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), category = "admin", hours = 2,
        });

        response.StatusCode.Should().Be(expected);
    }

    // ── task creation: only in projects PMO has added them to ──────────────────────────

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task HR_and_accountant_can_create_a_self_assigned_task_only_in_a_project_they_are_a_member_of(string role)
    {
        var viewer = await SeedEngineerAsync(EmailFor(role, "create"), role);
        var client = await AuthenticatedClientAsync(EmailFor(role, "create"));
        var other = await SeedEngineerAsync(EmailFor(role, "create_other"), Roles.Engineer);
        var team = await SeedTeamAsync($"ROV create team {role}");
        var memberProject = await SeedProjectAsync($"ROV member project {role}", team.Id);
        var otherProject = await SeedProjectAsync($"ROV non-member project {role}", team.Id);
        await SeedProjectMemberAsync(memberProject.Id, viewer.Id);

        object Payload(Guid projectId) => new
        {
            title = "Filed by a read-only role", points = 5, assigneeId = other.Id, projectId,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
        };

        var allowed = await client.PostAsJsonAsync("/api/v1/tasks", Payload(memberProject.Id));
        var denied = await client.PostAsJsonAsync("/api/v1/tasks", Payload(otherProject.Id));

        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await allowed.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        created.AssigneeId.Should().Be(viewer.Id, "below Team Lead a task can only be assigned to its creator");
        created.Points.Should().Be(0, "an estimate from a role below Team Lead is dropped");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
