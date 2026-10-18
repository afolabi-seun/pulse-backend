using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Estimation;
using Pulse.Application.Teams;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Infrastructure.Persistence;
using Pulse.Infrastructure.Persistence.Migrations;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Teams;

/// <summary>A team can name its head. Two heads whose departments carry the same label are told apart by it, and the fill-in for existing teams only
/// acts where there is exactly one candidate.</summary>
[Collection("Integration")]
public class TeamHeadTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TeamHeadTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<TeamDto> TeamAsync(HttpClient client, Guid teamId)
    {
        var all = (await (await client.GetAsync("/api/v1/teams")).Content.ReadFromJsonAsync<ApiResponse<List<TeamDto>>>(JsonOpts))!.Data!;
        return all.Single(t => t.Id == teamId);
    }

    private async Task<Guid?> HeadIdInDbAsync(Guid teamId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId)).HeadId;
    }

    [Fact]
    public async Task The_PMO_can_name_clear_and_see_a_teams_head()
    {
        await SeedEngineerAsync("th_pmo@pulse.io", Roles.HeadOfPmo);
        var head = await SeedEngineerAsync("th_head@pulse.io", Roles.HeadOfInfraDevOps);
        var team = await SeedTeamAsync("Head naming team", department: "Infra");
        var pmo = await AuthenticatedClientAsync("th_pmo@pulse.io");

        (await pmo.PatchAsJsonAsync($"/api/v1/teams/{team.Id}", new { headId = head.Id })).StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = await TeamAsync(pmo, team.Id);
        listed.HeadId.Should().Be(head.Id);
        listed.HeadName.Should().Be(head.Name);

        (await pmo.PatchAsJsonAsync($"/api/v1/teams/{team.Id}", new { clearHead = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TeamAsync(pmo, team.Id)).HeadId.Should().BeNull();
    }

    [Fact]
    public async Task Only_someone_holding_a_head_role_can_be_named_and_an_unknown_person_cannot()
    {
        await SeedEngineerAsync("th2_pmo@pulse.io", Roles.HeadOfPmo);
        var engineer = await SeedEngineerAsync("th2_eng@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Head validation team");
        var pmo = await AuthenticatedClientAsync("th2_pmo@pulse.io");

        var notAHead = await pmo.PatchAsJsonAsync($"/api/v1/teams/{team.Id}", new { headId = engineer.Id });
        var unknown = await pmo.PatchAsJsonAsync($"/api/v1/teams/{team.Id}", new { headId = Guid.NewGuid() });

        notAHead.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await HeadIdInDbAsync(team.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_team_can_be_created_with_its_head()
    {
        await SeedEngineerAsync("th3_pmo@pulse.io", Roles.HeadOfPmo);
        var head = await SeedEngineerAsync("th3_head@pulse.io", Roles.HeadOfRnD);
        var pmo = await AuthenticatedClientAsync("th3_pmo@pulse.io");

        var response = await pmo.PostAsJsonAsync("/api/v1/teams", new { name = "Born with a head", headId = head.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts))!.Data!;
        (await HeadIdInDbAsync(created.Id)).Should().Be(head.Id);
    }

    [Fact]
    public async Task The_fill_in_names_a_head_only_where_exactly_one_head_is_in_the_department()
    {
        // Department "Solo": one head, two teams -> both filled. Department "Shared": two heads -> left alone (the case that needs a person to decide).
        var soloHead = await SeedEngineerAsync("fill_solo_head@pulse.io", Roles.HeadOfDesign);
        var soloA = await SeedTeamAsync("Fill solo A", department: "Fill-Solo");
        var soloB = await SeedTeamAsync("Fill solo B", department: "Fill-Solo");
        await AssignEngineerToTeamAsync(soloHead.Id, soloA.Id);

        var rnd = await SeedTeamAsync("Fill shared R&D", department: "Fill-Shared");
        var infra = await SeedTeamAsync("Fill shared Infra", department: "Fill-Shared");
        var seun = await SeedEngineerAsync("fill_seun@pulse.io", Roles.HeadOfRnD);
        var felix = await SeedEngineerAsync("fill_felix@pulse.io", Roles.HeadOfInfraDevOps);
        await AssignEngineerToTeamAsync(seun.Id, rnd.Id);
        await AssignEngineerToTeamAsync(felix.Id, infra.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlRawAsync(AddTeamHead.BackfillSql);
        }

        (await HeadIdInDbAsync(soloA.Id)).Should().Be(soloHead.Id);
        (await HeadIdInDbAsync(soloB.Id)).Should().Be(soloHead.Id, "the head of a department heads each of its teams");
        (await HeadIdInDbAsync(rnd.Id)).Should().BeNull("two heads share this label, so a person has to choose");
        (await HeadIdInDbAsync(infra.Id)).Should().BeNull();
    }

    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_fill_in_never_mixes_organizations_a_label_shared_across_organizations_is_not_ambiguity()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid orgB;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Pulse.Domain.Organizations.Organization.Create("Head org B", $"head-b-{suffix}");
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            orgB = org.Id;
        }

        // Both organizations have a department called "Cross-Org" with one head each.
        var headA = await SeedEngineerAsync($"xorg_head_a_{suffix}@pulse.io", Roles.HeadOfRnD);
        var teamA = await SeedTeamAsync($"Cross A {suffix}", department: $"Cross-Org-{suffix}");
        await AssignEngineerToTeamAsync(headA.Id, teamA.Id);

        var headB = await SeedEngineerAsync($"xorg_head_b_{suffix}@pulse.io", Roles.HeadOfDesign);
        var teamB = await SeedTeamAsync($"Cross B {suffix}", department: $"Cross-Org-{suffix}");
        await AssignEngineerToTeamAsync(headB.Id, teamB.Id);
        await MoveToOrganizationAsync(teamB, orgB);
        await MoveToOrganizationAsync(headB, orgB);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlRawAsync(AddTeamHead.BackfillSql);
        }

        (await HeadIdInDbAsync(teamA.Id)).Should().Be(headA.Id, "organization A has exactly one head in this department");
    }

    [Fact]
    public async Task With_two_heads_sharing_a_department_label_the_request_goes_only_to_the_head_the_team_names()
    {
        // The real shape: R&D and Infra both labelled "Engineering"; Seun heads R&D, Felix heads Infra.
        var rndLead = await SeedEngineerAsync("sh_rnd_lead@pulse.io", Roles.TeamLead);
        var rnd = await SeedTeamAsync("Shared label R&D", rndLead.Id, "Engineering");
        var seun = await SeedEngineerAsync("sh_seun@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(seun.Id, rnd.Id);

        var infraLead = await SeedEngineerAsync("sh_infra_lead@pulse.io", Roles.TeamLead);
        var infra = await SeedTeamAsync("Shared label Infra", infraLead.Id, "Engineering");
        var felix = await SeedEngineerAsync("sh_felix@pulse.io", Roles.HeadOfInfraDevOps);
        await AssignEngineerToTeamAsync(felix.Id, infra.Id);
        var engineer = await SeedEngineerAsync("sh_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, infra.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var t = await db.Teams.SingleAsync(x => x.Id == infra.Id);
            t.SetHead(felix.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Shared label project", infra.Id);
        var task = await SeedTaskAsync("Needs estimate", project.Id, points: 0, assigneeId: engineer.Id);
        var engineerClient = await AuthenticatedClientAsync("sh_eng@pulse.io");
        var leadClient = await AuthenticatedClientAsync("sh_infra_lead@pulse.io");
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);

        // After the lead has had a working hour the head tier opens: Felix, never Seun.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var back = DateTime.UtcNow.AddDays(-10);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE task_estimation_sessions SET submitted_at = {back} WHERE task_id = {task.Id}");
        }
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrganizationJobRunner<EstimateApprovalEscalationScanner>>()
                .RunForOrganizationAsync(Pulse.Domain.Organizations.Organization.DefaultId, CancellationToken.None);

        var felixClient = await AuthenticatedClientAsync("sh_felix@pulse.io");
        var seunClient = await AuthenticatedClientAsync("sh_seun@pulse.io");
        var asFelix = (await (await felixClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation")).Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts))!.Data!;
        asFelix.CanApprove.Should().BeTrue();
        asFelix.PendingApproverNames.Should().HaveCount(2, "the team's lead and Felix; had Seun been matched by the label it would be three");
        var asSeun = (await (await seunClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation")).Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts))!.Data!;
        asSeun.CanApprove.Should().BeFalse("Seun shares the department label but is not the head this team names");
    }
}
