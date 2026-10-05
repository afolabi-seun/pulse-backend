using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Users;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Users;

[Collection("Integration")]
public class UsersTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public UsersTests(PulseWebApplicationFactory factory) : base(factory) { }

    // CreateUserRequestValidator requires TeamId (a Guid, resolved via a dropdown of existing teams
    // in the real UI) — not a team name string.
    private static object NewUserPayload(string email, Guid teamId, string role = Roles.Engineer) => new
    {
        name = "New User",
        email,
        role,
        baselinePoints = 20,
        baselineCycleDays = 14,
        teamId
    };

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_list_users()
    {
        await SeedEngineerAsync("users_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("users_eng_denied@pulse.io");

        var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Product_manager_can_create_engineer_users()
    {
        // User management is [PmOrAbove], which now includes product_manager; per
        // Roles.CreatableByDeptHead(ProductManager), they can create engineer/team_lead users.
        await SeedEngineerAsync("users_pdm_create@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("users_pdm_create@pulse.io");
        var team = await SeedTeamAsync("Users PM Create Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("pdm_created_engineer@pulse.io", team.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Product_manager_cannot_create_head_level_users()
    {
        await SeedEngineerAsync("users_pdm_head_denied@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("users_pdm_head_denied@pulse.io");
        var team = await SeedTeamAsync("Users PM Head Denied Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("should_fail@pulse.io", team.Id, Roles.HeadOfPmo));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HeadOfPmo_can_create_executive_users()
    {
        // The isPmo branch (HeadOfPmo/ProjectManager) skips CreatableByDeptHead entirely, so
        // Executive — otherwise absent from every department head's creatable-roles set — is
        // reachable here. Regression guard: CreateUserRequestValidator used to omit
        // Roles.Executive from its whitelist entirely, so this failed with a 400 for everyone
        // regardless of role, before the controller's own authorization ever ran.
        await SeedEngineerAsync("users_pmo_create_exec@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("users_pmo_create_exec@pulse.io");
        var team = await SeedTeamAsync("Users PMO Create Exec Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("pmo_created_exec@pulse.io", team.Id, Roles.Executive));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.Role.Should().Be(Roles.Executive);
    }

    [Fact]
    public async Task ProjectManager_can_create_executive_users()
    {
        await SeedEngineerAsync("users_pm_create_exec@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("users_pm_create_exec@pulse.io");
        var team = await SeedTeamAsync("Users PM Create Exec Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("pm_created_exec@pulse.io", team.Id, Roles.Executive));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateUser_allows_no_team_for_the_executive_role()
    {
        // Executive is the one role explicitly designed to have no team — a real PMO hit
        // "Team is required" trying to create/import one before this was fixed.
        await SeedEngineerAsync("users_exec_noteam_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("users_exec_noteam_pmo@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "No Team Exec",
            email = "no_team_exec@pulse.io",
            role = Roles.Executive,
            baselinePoints = 20,
            baselineCycleDays = 14,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.TeamId.Should().BeNull();
    }

    [Fact]
    public async Task CreateUser_still_requires_team_for_non_executive_roles()
    {
        await SeedEngineerAsync("users_noteam_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("users_noteam_pmo@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "No Team Engineer",
            email = "no_team_eng2@pulse.io",
            role = Roles.Engineer,
            baselinePoints = 20,
            baselineCycleDays = 14,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HeadOfProduct_can_create_executive_users()
    {
        // HeadOfProduct joined Roles.UserManagementGlobalRoles alongside HeadOfPmo/ProjectManager —
        // matching the org-wide reach it already had everywhere else (time tracking, check-ins,
        // escalations, teams, reports, engineer lists), user management was the one place it had
        // been left out.
        await SeedEngineerAsync("users_hop_create_exec@pulse.io", Roles.HeadOfProduct);
        var client = await AuthenticatedClientAsync("users_hop_create_exec@pulse.io");
        var team = await SeedTeamAsync("Users HeadOfProduct Create Exec Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("hop_created_exec@pulse.io", team.Id, Roles.Executive));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task HeadOfProduct_sees_users_org_wide_not_just_their_own_department()
    {
        await SeedEngineerAsync("users_hop_list@pulse.io", Roles.HeadOfProduct);
        var client = await AuthenticatedClientAsync("users_hop_list@pulse.io");

        var otherDeptTeam = await SeedTeamAsync("Users HeadOfProduct Other Dept Team", department: "Engineering");
        var outsider = await SeedEngineerAsync("users_hop_list_outsider@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(outsider.Id, otherDeptTeam.Id);

        var response = await client.GetAsync("/api/v1/users?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<UserDto>>>(JsonOpts);
        body!.Data!.Items.Should().Contain(u => u.Id == outsider.Id,
            "HeadOfProduct should see users org-wide, not scoped to their own department");
    }

    [Fact]
    public async Task Department_head_cannot_create_executive_users()
    {
        // Roles.CreatableByDeptHead(HeadOfRnD) never includes Executive — unaffected by the
        // validator fix above, which only unblocked the isPmo (HeadOfPmo/ProjectManager) path.
        await SeedEngineerAsync("users_depthead_exec_denied@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_depthead_exec_denied@pulse.io");
        var team = await SeedTeamAsync("Users Dept Head Exec Denied Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("should_fail_exec@pulse.io", team.Id, Roles.Executive));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_cannot_access_users()
    {
        var response = await Client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_creates_user_returns_201()
    {
        await SeedEngineerAsync("users_head_create@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_head_create@pulse.io");
        var team = await SeedTeamAsync("Users Create Team");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("users_new1@pulse.io", team.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.Email.Should().Be("users_new1@pulse.io");
        body.Data.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateUser_returns_409_for_duplicate_email()
    {
        await SeedEngineerAsync("users_head_dup@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_head_dup@pulse.io");
        var team = await SeedTeamAsync("Users Dup Team");

        // First create succeeds
        await client.PostAsJsonAsync("/api/v1/users", NewUserPayload("users_dup_target@pulse.io", team.Id));
        // Second create with same email should conflict
        var second = await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("users_dup_target@pulse.io", team.Id));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateUser_returns_400_for_invalid_email()
    {
        await SeedEngineerAsync("users_head_inv@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_head_inv@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Bad Email",
            email = "not-an-email",
            role = Roles.Engineer,
            baselinePoints = 20,
            baselineCycleDays = 14
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── QA requires a discipline ─────────────────────────────────────────────

    [Fact]
    public async Task CreateUser_returns_400_when_QA_is_checked_with_no_discipline()
    {
        await SeedEngineerAsync("users_qa_nodisc@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_qa_nodisc@pulse.io");
        var team = await SeedTeamAsync("Users QA No Discipline Team");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "New QA",
            email = "users_qa_nodisc_target@pulse.io",
            role = Roles.Engineer,
            baselinePoints = 20,
            baselineCycleDays = 14,
            teamId = team.Id,
            isQa = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateUser_succeeds_when_QA_is_checked_with_a_discipline()
    {
        await SeedEngineerAsync("users_qa_disc@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_qa_disc@pulse.io");
        var team = await SeedTeamAsync("Users QA Discipline Team");

        var response = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "New QA",
            email = "users_qa_disc_target@pulse.io",
            role = Roles.Engineer,
            baselinePoints = 20,
            baselineCycleDays = 14,
            teamId = team.Id,
            isQa = true,
            discipline = "Backend",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.IsQa.Should().BeTrue();
        body.Data.Discipline.Should().Be("backend");
    }

    [Fact]
    public async Task UpdateUser_returns_422_when_checking_QA_on_a_user_with_no_discipline()
    {
        var target = await SeedEngineerAsync("users_qa_update_nodisc@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("users_qa_update_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_qa_update_head@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/users/{target.Id}", new { isQa = true });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateUser_succeeds_when_checking_QA_and_setting_a_discipline_together()
    {
        var target = await SeedEngineerAsync("users_qa_update_disc@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("users_qa_update_disc_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_qa_update_disc_head@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/users/{target.Id}",
            new { isQa = true, discipline = "Backend" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.IsQa.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUser_succeeds_checking_QA_when_a_discipline_was_already_set()
    {
        var target = await SeedEngineerAsync("users_qa_update_predisc@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("users_qa_update_predisc_head@pulse.io", Roles.HeadOfRnD);
        var headClient = await AuthenticatedClientAsync("users_qa_update_predisc_head@pulse.io");
        await headClient.PatchAsJsonAsync($"/api/v1/users/{target.Id}", new { discipline = "Frontend" });

        var response = await headClient.PatchAsJsonAsync($"/api/v1/users/{target.Id}", new { isQa = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListUsers_isQa_filter_returns_only_QA_engineers()
    {
        await SeedEngineerAsync("users_list_qa_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_list_qa_head@pulse.io");
        var qaEngineer = await SeedEngineerAsync("users_list_qa_yes@pulse.io", Roles.Engineer, isQa: true);
        var nonQaEngineer = await SeedEngineerAsync("users_list_qa_no@pulse.io", Roles.Engineer);

        var response = await client.GetAsync("/api/v1/users?isQa=true&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<UserDto>>>(JsonOpts);
        var ids = body!.Data!.Items.Select(u => u.Id).ToList();
        ids.Should().Contain(qaEngineer.Id);
        ids.Should().NotContain(nonQaEngineer.Id);
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_list_users()
    {
        await SeedEngineerAsync("users_head_list@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_head_list@pulse.io");

        var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // ListUsers returns a paged result, not a bare array.
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<UserDto>>>(JsonOpts);
        body!.Data.Should().NotBeNull();
    }

    // ── deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_deactivate_user()
    {
        await SeedEngineerAsync("users_head_deact@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("users_head_deact@pulse.io");
        var team = await SeedTeamAsync("Users Deactivate Team");

        var created = (await (await client.PostAsJsonAsync("/api/v1/users",
            NewUserPayload("users_deact_target@pulse.io", team.Id)))
            .Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts))!.Data!;

        var deleteResp = await client.DeleteAsync($"/api/v1/users/{created.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── unlock ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_unlock_locked_account()
    {
        var locked = await SeedEngineerAsync("users_locked@pulse.io", Roles.Engineer);
        // Trigger 5 failed logins to lock the account
        for (var i = 0; i < 5; i++)
            await Client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = "users_locked@pulse.io", password = "wrong" });

        await SeedEngineerAsync("users_unlock_head@pulse.io", Roles.HeadOfRnD);
        var headClient = await AuthenticatedClientAsync("users_unlock_head@pulse.io");

        var unlockResp = await headClient.PatchAsJsonAsync($"/api/v1/users/{locked.Id}",
            new { unlockAccount = true });

        unlockResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await unlockResp.Content.ReadFromJsonAsync<ApiResponse<UserDto>>(JsonOpts);
        body!.Data!.LockedUntil.Should().BeNull();
    }

    // ── executive read-only access ───────────────────────────────────────────

    [Fact]
    public async Task Executive_can_list_and_view_users_but_cannot_create_update_or_delete()
    {
        var target = await SeedEngineerAsync("users_exec_target@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Exec Users Team");
        await SeedEngineerAsync("users_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("users_exec@pulse.io");

        var listResp = await client.GetAsync("/api/v1/users");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResp = await client.GetAsync($"/api/v1/users/{target.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var createResp = await client.PostAsJsonAsync("/api/v1/users", NewUserPayload("users_exec_cant_create@pulse.io", team.Id));
        createResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var updateResp = await client.PatchAsJsonAsync($"/api/v1/users/{target.Id}", new { isActive = false });
        updateResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var deleteResp = await client.DeleteAsync($"/api/v1/users/{target.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
