using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Engineers;
using Pulse.Application.Engineers.Queries;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Engineers;

[Collection("Integration")]
public class EngineersTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public EngineersTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_list_engineers()
    {
        var response = await Client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_list_engineers()
    {
        await SeedEngineerAsync("eng_list_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_list_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_can_list_engineers()
    {
        await SeedEngineerAsync("eng_list_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("eng_list_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Executive_can_list_engineers_org_wide()
    {
        await SeedEngineerAsync("eng_list_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("eng_list_exec@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TeamLead_can_list_engineers_scoped_to_their_own_team()
    {
        var lead = await SeedEngineerAsync("eng_list_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Lead's team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var teammate = await SeedEngineerAsync("eng_list_teammate@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(teammate.Id, team.Id);
        var otherTeam = await SeedTeamAsync("Other team");
        var stranger = await SeedEngineerAsync("eng_list_stranger@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(stranger.Id, otherTeam.Id);

        var client = await AuthenticatedClientAsync("eng_list_lead@pulse.io");
        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<EngineerDto>>>(JsonOpts);
        var ids = body!.Data!.Select(e => e.Id).ToList();
        ids.Should().Contain(lead.Id).And.Contain(teammate.Id);
        ids.Should().NotContain(stranger.Id);
    }

    [Fact]
    public async Task TeamLead_lists_their_led_team_even_when_personally_a_member_of_a_different_team()
    {
        // A team's lead is never auto-added as one of its own members (creating/updating a team
        // never assigns that), so a lead's own membership can point somewhere else entirely — this
        // reproduces the reported bug where a team lead couldn't reassign a task to their own
        // teammate because the roster was scoped by that stale membership instead of leadership.
        var lead = await SeedEngineerAsync("eng_list_led_lead@pulse.io", Roles.TeamLead);
        var ledTeam = await SeedTeamAsync("Actually Led Team", lead.Id);
        var teammate = await SeedEngineerAsync("eng_list_led_teammate@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(teammate.Id, ledTeam.Id);

        var staleTeam = await SeedTeamAsync("Stale Membership Team");
        await AssignEngineerToTeamAsync(lead.Id, staleTeam.Id);
        var staleTeammate = await SeedEngineerAsync("eng_list_led_stale@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(staleTeammate.Id, staleTeam.Id);

        var client = await AuthenticatedClientAsync("eng_list_led_lead@pulse.io");
        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<EngineerDto>>>(JsonOpts);
        var ids = body!.Data!.Select(e => e.Id).ToList();
        ids.Should().Contain(teammate.Id);
        ids.Should().NotContain(staleTeammate.Id);
    }

    [Fact]
    public async Task TeamLead_can_view_a_teammate_but_not_an_engineer_on_another_team()
    {
        var lead = await SeedEngineerAsync("eng_get_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Get Lead's team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var teammate = await SeedEngineerAsync("eng_get_teammate@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(teammate.Id, team.Id);
        var stranger = await SeedEngineerAsync("eng_get_stranger@pulse.io", Roles.Engineer);

        var client = await AuthenticatedClientAsync("eng_get_lead@pulse.io");

        var teammateResp = await client.GetAsync($"/api/v1/engineers/{teammate.Id}");
        teammateResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var strangerResp = await client.GetAsync($"/api/v1/engineers/{stranger.Id}");
        strangerResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEngineerThroughput_returns_200_for_self()
    {
        var engineer = await SeedEngineerAsync("eng_throughput_self@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_throughput_self@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{engineer.Id}/throughput");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetEngineerThroughput_returns_403_for_an_unrelated_engineer()
    {
        var target = await SeedEngineerAsync("eng_throughput_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_throughput_stranger@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_throughput_stranger@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{target.Id}/throughput");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListEngineers_returns_list_of_engineers()
    {
        await SeedEngineerAsync("eng_list2_pm@pulse.io", Roles.ProjectManager);
        await SeedEngineerAsync("eng_list2_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_list2_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<EngineerDto>>>(JsonOpts);
        body!.Status.Should().Be("success");
        body.Data.Should().NotBeEmpty();
    }

    // ── get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEngineer_returns_200_with_engineer_data()
    {
        var eng = await SeedEngineerAsync("eng_get_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("eng_get_pm@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{eng.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EngineerDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(eng.Id);
        body.Data.Email.Should().Be("eng_get_pm@pulse.io");
    }

    [Fact]
    public async Task GetEngineer_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("eng_404_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("eng_404_pm@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEngineer_returns_200_when_an_engineer_requests_their_own_record()
    {
        var eng = await SeedEngineerAsync("eng_get_self@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_get_self@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{eng.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EngineerDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(eng.Id);
    }

    [Fact]
    public async Task GetEngineer_returns_403_when_an_engineer_requests_another_engineers_record()
    {
        var target = await SeedEngineerAsync("eng_get_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_get_actor@pulse.io", Roles.Engineer);
        var actorClient = await AuthenticatedClientAsync("eng_get_actor@pulse.io");

        var response = await actorClient.GetAsync($"/api/v1/engineers/{target.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEngineer_PM_can_view_another_engineers_record()
    {
        var target = await SeedEngineerAsync("eng_get_target_pm@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_get_pm2@pulse.io", Roles.ProjectManager);
        var pmClient = await AuthenticatedClientAsync("eng_get_pm2@pulse.io");

        var response = await pmClient.GetAsync($"/api/v1/engineers/{target.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── overwork signals ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSignals_returns_200_for_own_engineer()
    {
        var eng = await SeedEngineerAsync("eng_signals_self@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("eng_signals_self@pulse.io");

        var response = await client.GetAsync($"/api/v1/engineers/{eng.Id}/signals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
        body.Data!.LoadVsBaseline.Should().NotBeNull();
        body.Data.Concurrent.Should().NotBeNull();
        body.Data.StaleInProgress.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSignals_returns_403_for_another_engineers_signals()
    {
        var target = await SeedEngineerAsync("eng_signals_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_signals_actor@pulse.io", Roles.Engineer);
        var actorClient = await AuthenticatedClientAsync("eng_signals_actor@pulse.io");

        var response = await actorClient.GetAsync($"/api/v1/engineers/{target.Id}/signals");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSignals_PM_can_view_any_engineers_signals()
    {
        var eng = await SeedEngineerAsync("eng_signals_target_pm@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_signals_pm@pulse.io", Roles.ProjectManager);
        var pmClient = await AuthenticatedClientAsync("eng_signals_pm@pulse.io");

        var response = await pmClient.GetAsync($"/api/v1/engineers/{eng.Id}/signals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSignals_reflects_the_engineers_department_threshold_override()
    {
        var team = await SeedTeamAsync("Signals Dept Team", department: "Regulatory Reporting Test Dept");
        var eng = await SeedEngineerAsync("eng_signals_dept@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(eng.Id, team.Id);
        var project = await SeedProjectAsync("Signals Dept Project");
        // Global ratio (1.3) x baseline (20) = 26 -> 27 pts trips it. Spread across three tasks —
        // PulseTask caps a single task at 13 points (the story-point scale), so no single task
        // can reach 27 on its own.
        await SeedTaskAsync("Loaded task 1", project.Id, points: 13, dueDaysFromNow: 5, assigneeId: eng.Id);
        await SeedTaskAsync("Loaded task 2", project.Id, points: 13, dueDaysFromNow: 5, assigneeId: eng.Id);
        await SeedTaskAsync("Loaded task 3", project.Id, points: 1, dueDaysFromNow: 5, assigneeId: eng.Id);

        var engClient = await AuthenticatedClientAsync("eng_signals_dept@pulse.io");
        var beforeResp = await engClient.GetAsync($"/api/v1/engineers/{eng.Id}/signals");
        var beforeBody = await beforeResp.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        beforeBody!.Data!.LoadVsBaseline.Tripped.Should().BeTrue("27 pts exceeds the global 26-pt threshold");

        await SeedEngineerAsync("eng_signals_dept_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("eng_signals_dept_pmo@pulse.io");
        var putResp = await pmoClient.PutAsJsonAsync(
            $"/api/v1/thresholds/departments/{Uri.EscapeDataString(team.Department!)}",
            new { loadVsBaselineRatio = 2.0 });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterResp = await engClient.GetAsync($"/api/v1/engineers/{eng.Id}/signals");
        var afterBody = await afterResp.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        afterBody!.Data!.LoadVsBaseline.Tripped.Should().BeFalse("the department override raised the threshold to 40 pts");
    }

    // ── baseline update ───────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_update_engineer_baseline()
    {
        var eng = await SeedEngineerAsync("eng_baseline_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_baseline_head@pulse.io", Roles.HeadOfRnD);
        var headClient = await AuthenticatedClientAsync("eng_baseline_head@pulse.io");

        var response = await headClient.PatchAsJsonAsync($"/api/v1/engineers/{eng.Id}",
            new { baselinePoints = 30, baselineCycleDays = 21 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EngineerDto>>(JsonOpts);
        body!.Data!.BaselinePoints.Should().Be(30);
        body.Data.BaselineCycleDays.Should().Be(21);
    }

    [Fact]
    public async Task Engineer_cannot_update_another_engineers_baseline()
    {
        var target = await SeedEngineerAsync("eng_baseline_denied_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_baseline_denied_actor@pulse.io", Roles.Engineer);
        var actorClient = await AuthenticatedClientAsync("eng_baseline_denied_actor@pulse.io");

        var response = await actorClient.PatchAsJsonAsync($"/api/v1/engineers/{target.Id}",
            new { baselinePoints = 30, baselineCycleDays = 21 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_can_update_engineer_baseline()
    {
        // PMO (project_manager) gained this alongside every department head — see
        // CapabilityRegistry's AnyHead+PmoOnly pairing on UpdateBaseline; PM_can_update_baseline
        // in MultiHeadRoleTests.cs covers the same fix.
        var target = await SeedEngineerAsync("eng_baseline_pm_target@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("eng_baseline_pm@pulse.io", Roles.ProjectManager);
        var pmClient = await AuthenticatedClientAsync("eng_baseline_pm@pulse.io");

        var response = await pmClient.PatchAsJsonAsync($"/api/v1/engineers/{target.Id}",
            new { baselinePoints = 30, baselineCycleDays = 21 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── loan candidates ──────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_list_loan_candidates()
    {
        await SeedEngineerAsync("loancand_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("loancand_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers/loan-candidates");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TeamLead_loan_candidates_excludes_own_department_and_includes_others()
    {
        var lead = await SeedEngineerAsync("loancand_lead@pulse.io", Roles.TeamLead);
        var peer = await SeedEngineerAsync("loancand_peer@pulse.io", Roles.Engineer);
        var outsider = await SeedEngineerAsync("loancand_outsider@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Loancand Team A", lead.Id, department: "Engineering");
        var teamB = await SeedTeamAsync("Loancand Team B", department: "Design");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(lead.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(peer.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(outsider.Id))!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var leadClient = await AuthenticatedClientAsync("loancand_lead@pulse.io");
        var response = await leadClient.GetAsync("/api/v1/engineers/loan-candidates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<EngineerDto>>>(JsonOpts);
        var ids = body!.Data!.Select(e => e.Id).ToList();
        ids.Should().Contain(outsider.Id);
        ids.Should().NotContain(lead.Id);
        ids.Should().NotContain(peer.Id);
    }

    [Fact]
    public async Task TeamLead_loan_candidates_are_scoped_by_the_led_department_not_stale_membership()
    {
        // Mirrors LoanTaskCommand's own same-department check, which compares against the team the
        // lead actually leads (Team.TeamLeadId) — never their own possibly-stale team membership.
        // Before the fix, this handler used the membership-based department instead, so a lead
        // whose membership pointed at a different department would wrongly see their real
        // teammate as loanable and wrongly exclude engineers in their real (led) department.
        var lead = await SeedEngineerAsync("loancand_led_lead@pulse.io", Roles.TeamLead);
        var realPeer = await SeedEngineerAsync("loancand_led_realpeer@pulse.io", Roles.Engineer);
        var staleDeptPeer = await SeedEngineerAsync("loancand_led_staledeptpeer@pulse.io", Roles.Engineer);
        var realTeam = await SeedTeamAsync("Loancand Real Led Team", lead.Id, department: "Engineering");
        var staleTeam = await SeedTeamAsync("Loancand Stale Membership Team", department: "Design");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(lead.Id))!.AssignToTeam(staleTeam.Id); // stale membership, not the led team
            (await db.Engineers.FindAsync(realPeer.Id))!.AssignToTeam(realTeam.Id);
            (await db.Engineers.FindAsync(staleDeptPeer.Id))!.AssignToTeam(staleTeam.Id);
            await db.SaveChangesAsync();
        }

        var leadClient = await AuthenticatedClientAsync("loancand_led_lead@pulse.io");
        var response = await leadClient.GetAsync("/api/v1/engineers/loan-candidates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<EngineerDto>>>(JsonOpts);
        var ids = body!.Data!.Select(e => e.Id).ToList();
        ids.Should().NotContain(realPeer.Id);
        ids.Should().Contain(staleDeptPeer.Id);
    }

    // ── qa candidates ────────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_list_qa_candidates()
    {
        await SeedEngineerAsync("qacand_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("qacand_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers/qa-candidates");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HeadOfEngineering_sees_a_QA_engineer_in_a_different_department_via_qa_candidates()
    {
        var head = await SeedEngineerAsync("qacand_head_eng@pulse.io", Roles.HeadOfRnD);
        var productQaReviewer = await SeedEngineerAsync("qacand_product_qa@pulse.io", Roles.Engineer, isQa: true);
        var nonQaPeer = await SeedEngineerAsync("qacand_non_qa@pulse.io", Roles.Engineer);
        var engTeam = await SeedTeamAsync("Qacand Eng Team", department: "Engineering");
        var productTeam = await SeedTeamAsync("Qacand Product Team", department: "Product");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(head.Id))!.AssignToTeam(engTeam.Id);
            var reviewer = (await db.Engineers.FindAsync(productQaReviewer.Id))!;
            reviewer.AssignToTeam(productTeam.Id);
            reviewer.SetDiscipline(Discipline.Product);
            (await db.Engineers.FindAsync(nonQaPeer.Id))!.AssignToTeam(productTeam.Id);
            await db.SaveChangesAsync();
        }

        var headClient = await AuthenticatedClientAsync("qacand_head_eng@pulse.io");
        var response = await headClient.GetAsync("/api/v1/engineers/qa-candidates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<EngineerDto>>>(JsonOpts);
        var ids = body!.Data!.Select(e => e.Id).ToList();
        ids.Should().Contain(productQaReviewer.Id, "a Head of Engineering must see a QA reviewer outside their own department");
        ids.Should().NotContain(nonQaPeer.Id, "a non-QA engineer is never a valid QA assignment target");
    }

    // ── project-assignable ───────────────────────────────────────────────────

    [Fact]
    public async Task ProjectAssignable_returns_the_owner_teams_roster_even_for_a_head_in_a_different_department()
    {
        var head = await SeedEngineerAsync("projassign_head@pulse.io", Roles.HeadOfFunctional);
        var ownerTeam = await SeedTeamAsync("ProjAssign Owner Team", department: "Core Banking");
        var coreBankingEngineer = await SeedEngineerAsync("projassign_cb_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(coreBankingEngineer.Id, ownerTeam.Id);
        var project = await SeedProjectAsync("Finsys CBS", ownerTeam.Id);
        // The head follows the project (or is a member) so CanAccessProjectAsync allows it —
        // otherwise this would 403 regardless of the fix under test.
        await SeedProjectMemberAsync(project.Id, head.Id);

        var client = await AuthenticatedClientAsync("projassign_head@pulse.io");
        var response = await client.GetAsync($"/api/v1/engineers/project-assignable/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<EngineerDto>>>(JsonOpts);
        body!.Data!.Select(e => e.Id).Should().Contain(coreBankingEngineer.Id,
            "a head managing a project outside their own department must see that project's real team");
    }

    [Fact]
    public async Task ProjectAssignable_returns_403_when_the_caller_cannot_access_the_project()
    {
        // A department head with no team assigned is deliberately treated as unscoped ("sees
        // all") by CanAccessProjectAsync — the outsider needs a real team in a different
        // department to be genuinely excluded, not just a bare head role.
        var outsider = await SeedEngineerAsync("projassign_denied@pulse.io", Roles.HeadOfFunctional);
        var outsiderTeam = await SeedTeamAsync("ProjAssign Outsider Team", department: "Functional");
        await AssignEngineerToTeamAsync(outsider.Id, outsiderTeam.Id);
        var ownerTeam = await SeedTeamAsync("ProjAssign Denied Owner Team", department: "Core Banking");
        var project = await SeedProjectAsync("Inaccessible project", ownerTeam.Id);

        var client = await AuthenticatedClientAsync("projassign_denied@pulse.io");
        var response = await client.GetAsync($"/api/v1/engineers/project-assignable/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EngineersPage_excludes_a_role_that_can_never_carry_a_delivery_workload()
    {
        var team = await SeedTeamAsync("Engineers Page Workload Team");
        var engineer = await SeedEngineerAsync("engpage_workload_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var pmoOnTeam = await SeedEngineerAsync("engpage_workload_pmo@pulse.io", Roles.HeadOfPmo);
        await AssignEngineerToTeamAsync(pmoOnTeam.Id, team.Id);
        var client = await AuthenticatedClientAsync("engpage_workload_pmo@pulse.io");

        var response = await client.GetAsync("/api/v1/engineers/page?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EngineerListPageDto>>(JsonOpts);
        body!.Data!.Items.Select(e => e.Id).Should().Contain(engineer.Id);
        body.Data.Items.Select(e => e.Id).Should().NotContain(pmoOnTeam.Id);
    }

    [Fact]
    public async Task EngineersPage_lists_the_leads_led_team_even_when_personally_a_member_of_a_different_team()
    {
        var lead = await SeedEngineerAsync("engpage_led_lead@pulse.io", Roles.TeamLead);
        var ledTeam = await SeedTeamAsync("EngPage Actually Led Team", lead.Id);
        var teammate = await SeedEngineerAsync("engpage_led_teammate@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(teammate.Id, ledTeam.Id);

        var staleTeam = await SeedTeamAsync("EngPage Stale Membership Team");
        await AssignEngineerToTeamAsync(lead.Id, staleTeam.Id);
        var staleTeammate = await SeedEngineerAsync("engpage_led_stale@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(staleTeammate.Id, staleTeam.Id);

        var client = await AuthenticatedClientAsync("engpage_led_lead@pulse.io");
        var response = await client.GetAsync("/api/v1/engineers/page?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<EngineerListPageDto>>(JsonOpts);
        var ids = body!.Data!.Items.Select(e => e.Id).ToList();
        ids.Should().Contain(teammate.Id);
        ids.Should().NotContain(staleTeammate.Id);
    }
}
