using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Projects;

[Collection("Integration")]
public class ProjectsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ProjectsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_create_project()
    {
        await SeedEngineerAsync("proj_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("proj_eng_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Forbidden" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProductManager_can_create_project()
    {
        await SeedEngineerAsync("proj_create_pdm@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("proj_create_pdm@pulse.io");
        var team = await SeedTeamAsync("Product Manager Team");

        var response = await client.PostAsJsonAsync("/api/v1/projects",
            new { name = "PdM-created project", ownerTeamId = team.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Unauthenticated_cannot_list_projects()
    {
        var response = await Client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TeamLead_can_list_projects_scoped_to_their_own_team()
    {
        var lead    = await SeedEngineerAsync("proj_list_teamlead@pulse.io", Roles.TeamLead);
        var ownTeam = await SeedTeamAsync("Proj list own team", lead.Id);
        var otherTeam = await SeedTeamAsync("Proj list other team");
        await AssignEngineerToTeamAsync(lead.Id, ownTeam.Id);
        var ownProject   = await SeedProjectAsync("Own team project", ownTeam.Id);
        var otherProject = await SeedProjectAsync("Other team project", otherTeam.Id);
        var client = await AuthenticatedClientAsync("proj_list_teamlead@pulse.io");

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        var byId = body!.Data!.ToDictionary(p => p.Id);
        byId[ownProject.Id].CanAccess.Should().BeTrue();
        byId[otherProject.Id].CanAccess.Should().BeFalse();
    }

    [Fact]
    public async Task TeamLead_project_list_shows_canAccess_for_a_project_their_engineer_is_a_member_of()
    {
        // GetTask/GetProject already allow this via ProjectAccessPolicy.ProjectHasLedTeamMemberAsync
        // (direct navigation returns 200) — the list's canAccess flag must agree, or the Projects
        // filter dropdown would hide a project the lead can actually open.
        var lead      = await SeedEngineerAsync("proj_list_parity_lead@pulse.io", Roles.TeamLead);
        var ownTeam   = await SeedTeamAsync("Proj list parity own team", lead.Id);
        var otherTeam = await SeedTeamAsync("Proj list parity other team");
        var report    = await SeedEngineerAsync("proj_list_parity_report@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(lead.Id, ownTeam.Id);
        await AssignEngineerToTeamAsync(report.Id, ownTeam.Id);
        var otherProject = await SeedProjectAsync("Parity other team project", otherTeam.Id);
        await SeedProjectMemberAsync(otherProject.Id, report.Id);
        var client = await AuthenticatedClientAsync("proj_list_parity_lead@pulse.io");

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        body!.Data!.Single(p => p.Id == otherProject.Id).CanAccess.Should().BeTrue();
    }

    [Fact]
    public async Task DepartmentHead_is_denied_for_a_project_their_engineer_is_a_member_of_until_they_follow_it()
    {
        // Being merely adjacent to a project (one of my engineers happens to be a member) is no
        // longer enough — a department head must own the project or deliberately follow it.
        var head        = await SeedEngineerAsync("proj_list_head_parity@pulse.io", Roles.HeadOfRnD);
        var headTeam    = await SeedTeamAsync("Parity RnD team", null, department: "RnD");
        var otherTeam   = await SeedTeamAsync("Parity Design team", null, department: "Design");
        var report      = await SeedEngineerAsync("proj_list_head_parity_report@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(head.Id, headTeam.Id);
        await AssignEngineerToTeamAsync(report.Id, headTeam.Id);
        var otherProject = await SeedProjectAsync("Parity out of dept project", otherTeam.Id);
        await SeedProjectMemberAsync(otherProject.Id, report.Id);
        var client = await AuthenticatedClientAsync("proj_list_head_parity@pulse.io");

        var beforeFollow = await client.GetAsync("/api/v1/projects");
        beforeFollow.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeBody = await beforeFollow.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        beforeBody!.Data!.Single(p => p.Id == otherProject.Id).CanAccess.Should().BeFalse();

        var followResponse = await client.PostAsJsonAsync($"/api/v1/projects/{otherProject.Id}/follow", new { });
        followResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/v1/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProjectDto>>>(JsonOpts);
        body!.Data!.Single(p => p.Id == otherProject.Id).CanAccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("project_manager")]
    [InlineData("product_manager")]
    public async Task ProjectManager_and_ProductManager_can_follow_a_project_with_no_department_member(string role)
    {
        // Unlike a department head, PM/ProductManager already have org-wide access (they don't
        // need to follow to see the project), and — unlike a department head who needs one of
        // their own department's engineers on the project — they have no single department for
        // that check to even mean anything, so they must be exempt from it entirely.
        await SeedEngineerAsync($"proj_follow_{role}@pulse.io", role);
        var client = await AuthenticatedClientAsync($"proj_follow_{role}@pulse.io");
        var otherTeam = await SeedTeamAsync($"Follow no-dept-member team {role}", null, department: "Design");
        var project = await SeedProjectAsync($"Follow no-dept-member project {role}", otherTeam.Id);

        var followResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/follow", new { });
        followResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await client.GetAsync("/api/v1/projects/followed");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await listResponse.Content.ReadFromJsonAsync<ApiResponse<List<FollowedProjectDto>>>(JsonOpts);
        listBody!.Data!.Should().Contain(p => p.ProjectId == project.Id);

        var unfollowResponse = await client.DeleteAsync($"/api/v1/projects/{project.Id}/follow");
        unfollowResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_returns_201_with_project_data()
    {
        await SeedEngineerAsync("proj_create_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_create_pm@pulse.io");
        var team = await SeedTeamAsync("Alpha Platform Team");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Alpha Platform",
            description = "Core platform work",
            ownerTeamId = team.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Name.Should().Be("Alpha Platform");
        body.Data.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateProject_returns_400_when_name_is_missing()
    {
        await SeedEngineerAsync("proj_validation@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_validation@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { description = "No name" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProject_returns_200_with_project_data()
    {
        await SeedEngineerAsync("proj_get_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_get_pm@pulse.io");
        var team = await SeedTeamAsync("GetMe Team");

        var created = (await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "GetMe project", ownerTeamId = team.Id }))
            .Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts))!.Data!;

        var getResp = await client.GetAsync($"/api/v1/projects/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetProject_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("proj_404@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_404@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── update / archive ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_returns_400_when_ownerTeamId_is_missing()
    {
        await SeedEngineerAsync("proj_no_team@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_no_team@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "No team project" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateProject_can_set_and_clear_owner_team()
    {
        await SeedEngineerAsync("proj_reteam@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_reteam@pulse.io");
        var teamA = await SeedTeamAsync("Reteam A");
        var teamB = await SeedTeamAsync("Reteam B");

        var created = (await (await client.PostAsJsonAsync("/api/v1/projects",
            new { name = "Reassignable project", ownerTeamId = teamA.Id }))
            .Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts))!.Data!;

        var reassignResp = await client.PatchAsJsonAsync($"/api/v1/projects/{created.Id}",
            new { ownerTeamId = teamB.Id });
        var reassignBody = await reassignResp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        reassignBody!.Data!.OwnerTeamId.Should().Be(teamB.Id);

        var clearResp = await client.PatchAsJsonAsync($"/api/v1/projects/{created.Id}",
            new { clearOwnerTeam = true });
        var clearBody = await clearResp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        clearBody!.Data!.OwnerTeamId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProject_renames_project()
    {
        await SeedEngineerAsync("proj_update@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_update@pulse.io");
        var team = await SeedTeamAsync("Old Name Team");

        var created = (await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Old Name", ownerTeamId = team.Id }))
            .Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/projects/{created.Id}",
            new { name = "New Name" });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task ArchiveProject_sets_isActive_to_false()
    {
        await SeedEngineerAsync("proj_archive@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("proj_archive@pulse.io");
        var team = await SeedTeamAsync("Archive Team");

        var created = (await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Archive me", ownerTeamId = team.Id }))
            .Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/projects/{created.Id}",
            new { archive = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.IsActive.Should().BeFalse();
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_returns_204_for_empty_project()
    {
        await SeedEngineerAsync("proj_del_head@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("proj_del_head@pulse.io");

        var project = await SeedProjectAsync("Empty project to delete");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteProject_returns_422_when_project_has_tasks()
    {
        await SeedEngineerAsync("proj_del_blocked_head@pulse.io", Roles.HeadOfPmo);
        await SeedEngineerAsync("proj_del_blocked_pm@pulse.io", Roles.ProjectManager);
        var headClient = await AuthenticatedClientAsync("proj_del_blocked_head@pulse.io");
        var pmClient = await AuthenticatedClientAsync("proj_del_blocked_pm@pulse.io");
        var team = await SeedTeamAsync("Non-empty Project Team");

        var project = (await (await pmClient.PostAsJsonAsync("/api/v1/projects",
            new { name = "Non-empty project", ownerTeamId = team.Id }))
            .Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts))!.Data!;

        // Add a task to the project
        await pmClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Blocking task",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        var deleteResp = await headClient.DeleteAsync($"/api/v1/projects/{project.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DeleteProject_returns_403_for_product_manager_role()
    {
        // Project deletion is [ProjectDeleterOrAbove] = head_of_pmo, project_manager, or
        // head_of_product; a product_manager (not head_of_product) is denied.
        await SeedEngineerAsync("proj_del_pdm_denied@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("proj_del_pdm_denied@pulse.io");
        var project = await SeedProjectAsync("PdM cannot delete");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteProject_returns_204_for_head_of_product()
    {
        await SeedEngineerAsync("proj_del_hop@pulse.io", Roles.HeadOfProduct);
        var client = await AuthenticatedClientAsync("proj_del_hop@pulse.io");
        var project = await SeedProjectAsync("Head of Product can delete");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── mine ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMyProjects_returns_projects_the_engineer_is_a_member_of()
    {
        var engineer = await SeedEngineerAsync("proj_mine_member@pulse.io", Roles.Engineer);
        var member    = await SeedProjectAsync("Mine — member project");
        var unrelated = await SeedProjectAsync("Mine — unrelated project");
        await SeedProjectMemberAsync(member.Id, engineer.Id);
        var client = await AuthenticatedClientAsync("proj_mine_member@pulse.io");

        var response = await client.GetAsync("/api/v1/projects/mine");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<MyProjectDto>>>(JsonOpts);
        body!.Data!.Should().ContainSingle(p => p.Id == member.Id);
        body.Data!.Should().NotContain(p => p.Id == unrelated.Id);
    }

    [Fact]
    public async Task GetMyProjects_returns_empty_list_for_engineer_with_no_projects()
    {
        await SeedEngineerAsync("proj_mine_none@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("proj_mine_none@pulse.io");

        var response = await client.GetAsync("/api/v1/projects/mine");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<MyProjectDto>>>(JsonOpts);
        body!.Data!.Should().BeEmpty();
    }

    // ── project code ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_with_no_code_derives_one_from_the_name()
    {
        await SeedEngineerAsync("proj_code_derive_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Code Derive Team");
        var client = await AuthenticatedClientAsync("proj_code_derive_pmo@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Notifications Platform", ownerTeamId = team.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Code.Should().Be("NOTIFI");
    }

    [Fact]
    public async Task CreateProject_with_an_explicit_code_uses_it()
    {
        await SeedEngineerAsync("proj_code_explicit_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Code Explicit Team");
        var client = await AuthenticatedClientAsync("proj_code_explicit_pmo@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Some Project", code = "SPROJ", ownerTeamId = team.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Code.Should().Be("SPROJ");
    }

    [Fact]
    public async Task CreateProject_rejects_a_code_already_in_use()
    {
        await SeedEngineerAsync("proj_code_dup_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Code Dup Team");
        var client = await AuthenticatedClientAsync("proj_code_dup_pmo@pulse.io");
        (await client.PostAsJsonAsync("/api/v1/projects", new { name = "First", code = "DUPE", ownerTeamId = team.Id }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Second", code = "DUPE", ownerTeamId = team.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_projects_that_derive_the_same_base_code_get_deduplicated_suffixes()
    {
        await SeedEngineerAsync("proj_code_collide_pmo@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("Code Collide Team");
        var client = await AuthenticatedClientAsync("proj_code_collide_pmo@pulse.io");

        var first = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Alpha Team Project", ownerTeamId = team.Id });
        var second = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Alpha Team Rebuild", ownerTeamId = team.Id });

        var firstBody = await first.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        var secondBody = await second.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        firstBody!.Data!.Code.Should().Be("ALPHAT");
        secondBody!.Data!.Code.Should().Be("ALPHAT2");
    }
}
