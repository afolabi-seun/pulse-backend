using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.MultiHeadRoles;

/// <summary>
/// Verifies that head_of_product, head_of_design, and head_of_pmo have the same
/// read-access permissions as head_of_rd on the endpoints opened in the multi-head
/// role expansion. Each endpoint that was previously [HeadOnly] (RD-only) and is
/// now [AnyHead] gets a theory that runs against all three new roles.
/// </summary>
[Collection("Integration")]
public class MultiHeadRoleTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public MultiHeadRoleTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── helpers ───────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> NewHeadRoles =>
    [
        [Roles.HeadOfProduct],
        [Roles.HeadOfDesign],
        [Roles.HeadOfPmo],
    ];

    private static string Tag(string role) => role.Replace("head_of_", "");

    // ── feedback ──────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_read_feedback(string role)
    {
        await SeedEngineerAsync($"mh_fb_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_fb_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/feedback");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to read feedback");
    }

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_read_feedback_patterns(string role)
    {
        await SeedEngineerAsync($"mh_fp_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_fp_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/feedback/patterns");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to read feedback patterns");
    }

    // ── reports ───────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_get_leadership_report(string role)
    {
        await SeedEngineerAsync($"mh_rpt_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_rpt_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to access the leadership report");
    }

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_download_leadership_report_pdf(string role)
    {
        await SeedEngineerAsync($"mh_pdf_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_pdf_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/reports/leadership/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to download the report PDF");
    }

    // ── audit log ─────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_cannot_read_audit_log(string role)
    {
        // The audit log endpoint remained [HeadOnly] (head_of_rd only) — not part of the expansion.
        await SeedEngineerAsync($"mh_aud_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_aud_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/audit-log");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: $"only head_of_rd may read the audit log, not {role}");
    }

    // ── vitals ─────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_read_vitals(string role)
    {
        await SeedEngineerAsync($"mh_pls_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_pls_{Tag(role)}@vitals.io");

        var response = await client.GetAsync("/api/v1/vitals");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to read vitals responses");
    }

    // ── teams ─────────────────────────────────────────────────────────────────

    // Team creation is [TeamCreatorOrAbove] (head_of_pmo, project_manager, head_of_product, or
    // product_manager) — head_of_design (and other non-Product, non-PMO heads) cannot.
    public static IEnumerable<object[]> NonPmoHeadRoles =>
    [
        [Roles.HeadOfDesign],
    ];

    [Fact]
    public async Task Head_of_pmo_can_create_team()
    {
        await SeedEngineerAsync("mh_tm_pmo@vitals.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("mh_tm_pmo@vitals.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams",
            new { name = $"MH PMO Team {Guid.NewGuid():N}"[..40], department = "Engineering" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Head_of_product_can_create_team()
    {
        await SeedEngineerAsync("mh_tm_product@vitals.io", Roles.HeadOfProduct);
        var client = await AuthenticatedClientAsync("mh_tm_product@vitals.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams",
            new { name = $"MH Product Team {Guid.NewGuid():N}"[..40], department = "Product" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory, MemberData(nameof(NonPmoHeadRoles))]
    public async Task Non_pmo_head_cannot_create_team(string role)
    {
        await SeedEngineerAsync($"mh_tm_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_tm_{Tag(role)}@vitals.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams",
            new { name = $"MH Team {role} {Guid.NewGuid():N}"[..40], department = "Engineering" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: $"team creation is [TeamCreatorOrAbove] and {role} is not in that set");
    }

    // ── project following ─────────────────────────────────────────────────────

    // ProjectFollow is team lead and above — every head role, including Head of PMO, can follow
    // (see CapabilityRegistry.ProjectFollow's doc comment: it's a personal bookmark list, not an
    // access grant, so it's harmless even for roles that already see every project).
    public static IEnumerable<object[]> FollowCapableHeadRoles =>
    [
        [Roles.HeadOfProduct],
        [Roles.HeadOfDesign],
        [Roles.HeadOfPmo],
    ];

    [Theory, MemberData(nameof(FollowCapableHeadRoles))]
    public async Task New_head_role_can_follow_and_unfollow_project(string role)
    {
        await SeedEngineerAsync($"mh_flw_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_flw_{Tag(role)}@vitals.io");
        var project = await SeedProjectAsync($"MH Follow Project {role}");

        var followResp = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/follow", new { });
        followResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to follow a project");

        var listResp = await client.GetAsync("/api/v1/projects/followed");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to list followed projects");

        var unfollowResp = await client.DeleteAsync($"/api/v1/projects/{project.Id}/follow");
        unfollowResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to unfollow a project");
    }

    // ── engineer baseline history ─────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_read_engineer_baseline_history(string role)
    {
        var engineer = await SeedEngineerAsync($"mh_bl_eng_{Tag(role)}@vitals.io");
        await SeedEngineerAsync($"mh_bl_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_bl_{Tag(role)}@vitals.io");

        var response = await client.GetAsync($"/api/v1/engineers/{engineer.Id}/baseline-history");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to read engineer baseline history");
    }

    // ── sprint delete ─────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_delete_planning_sprint(string role)
    {
        var lead   = await SeedEngineerAsync($"mh_sp_lead_{Tag(role)}@vitals.io", Roles.TeamLead);
        var team   = await SeedTeamAsync($"MH Sprint Team {role}", lead.Id);
        var sprint = await SeedSprintAsync($"MH Sprint {role}", team.Id);

        await SeedEngineerAsync($"mh_sp_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_sp_{Tag(role)}@vitals.io");

        var response = await client.DeleteAsync($"/api/v1/sprints/{sprint.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            because: $"{role} must be able to delete a planning sprint");
    }

    // ── engineer baseline update ──────────────────────────────────────────────

    [Theory, MemberData(nameof(NewHeadRoles))]
    public async Task New_head_role_can_update_engineer_baseline(string role)
    {
        var engineer = await SeedEngineerAsync($"mh_bu_eng_{Tag(role)}@vitals.io");
        await SeedEngineerAsync($"mh_bu_{Tag(role)}@vitals.io", role);
        var client = await AuthenticatedClientAsync($"mh_bu_{Tag(role)}@vitals.io");

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/engineers/{engineer.Id}",
            new { baselinePoints = 8, baselineCycleDays = 14 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{role} must be able to update an engineer's baseline");
    }

    // ── PMO (project_manager) can also manage baselines ─────────────────────────

    [Fact]
    public async Task Project_manager_can_read_engineer_baseline_history()
    {
        var engineer = await SeedEngineerAsync("mh_bl_eng_pm@vitals.io");
        await SeedEngineerAsync("mh_bl_pm@vitals.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("mh_bl_pm@vitals.io");

        var response = await client.GetAsync($"/api/v1/engineers/{engineer.Id}/baseline-history");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "PMO (project_manager) must be able to read engineer baseline history, same as any department head");
    }

    [Fact]
    public async Task Project_manager_can_update_engineer_baseline()
    {
        var engineer = await SeedEngineerAsync("mh_bu_eng_pm@vitals.io");
        await SeedEngineerAsync("mh_bu_pm@vitals.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("mh_bu_pm@vitals.io");

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/engineers/{engineer.Id}",
            new { baselinePoints = 8, baselineCycleDays = 14 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "PMO (project_manager) must be able to update an engineer's baseline, same as any department head");
    }

    // ── non-head still blocked ────────────────────────────────────────────────

    [Fact]
    public async Task Project_manager_cannot_read_feedback()
    {
        await SeedEngineerAsync("mh_pm_fb@vitals.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("mh_pm_fb@vitals.io");

        var response = await client.GetAsync("/api/v1/feedback");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Project_manager_cannot_access_leadership_report()
    {
        await SeedEngineerAsync("mh_pm_rpt@vitals.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("mh_pm_rpt@vitals.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Pulse.Domain.Sprints.Sprint> SeedSprintAsync(string name, Guid teamId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
        var sprint = Pulse.Domain.Sprints.Sprint.Create(
            teamId, null, name,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21)),
            null);
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        return sprint;
    }
}
