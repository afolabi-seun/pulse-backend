using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Automations;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Automations;

[Collection("Integration")]
public class AutomationRulesTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public AutomationRulesTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task TeamLead_can_create_list_update_and_delete_a_rule_for_their_own_team()
    {
        var lead = await SeedEngineerAsync("automation_rule_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Automation Rule Team", lead.Id);
        var client = await AuthenticatedClientAsync("automation_rule_lead@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/automation-rules", new
        {
            name = "Reassign stuck tasks",
            teamId = team.Id,
            thresholdDays = 5,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = (await created.Content.ReadFromJsonAsync<ApiResponse<AutomationRuleDto>>(JsonOpts))!.Data!;
        rule.TeamName.Should().Be("Automation Rule Team");

        var listResp = await client.GetAsync("/api/v1/automation-rules");
        var list = (await listResp.Content.ReadFromJsonAsync<ApiResponse<List<AutomationRuleDto>>>(JsonOpts))!.Data!;
        list.Should().ContainSingle(r => r.Id == rule.Id);

        var updateResp = await client.PatchAsJsonAsync($"/api/v1/automation-rules/{rule.Id}", new
        {
            name = "Renamed",
            thresholdDays = 10,
            isActive = false,
        });
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await updateResp.Content.ReadFromJsonAsync<ApiResponse<AutomationRuleDto>>(JsonOpts))!.Data!;
        updated.Name.Should().Be("Renamed");
        updated.ThresholdDays.Should().Be(10);
        updated.IsActive.Should().BeFalse();

        var deleteResp = await client.DeleteAsync($"/api/v1/automation-rules/{rule.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listAfterDelete = (await (await client.GetAsync("/api/v1/automation-rules")).Content.ReadFromJsonAsync<ApiResponse<List<AutomationRuleDto>>>(JsonOpts))!.Data!;
        listAfterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task TeamLead_cannot_create_a_rule_for_a_team_they_do_not_lead()
    {
        var lead = await SeedEngineerAsync("automation_rule_outsider@pulse.io", Roles.TeamLead);
        var otherTeam = await SeedTeamAsync("Someone Elses Automation Team", Guid.NewGuid());
        var client = await AuthenticatedClientAsync("automation_rule_outsider@pulse.io");

        var resp = await client.PostAsJsonAsync("/api/v1/automation-rules", new
        {
            name = "Watching someone else's team",
            teamId = otherTeam.Id,
            thresholdDays = 5,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_rejects_a_team_with_no_team_lead()
    {
        var lead = await SeedEngineerAsync("automation_rule_pm@pulse.io", Roles.ProjectManager);
        var leadlessTeam = await SeedTeamAsync("Leadless Automation Team", null);
        var client = await AuthenticatedClientAsync("automation_rule_pm@pulse.io");

        var resp = await client.PostAsJsonAsync("/api/v1/automation-rules", new
        {
            name = "No lead to reassign to",
            teamId = leadlessTeam.Id,
            thresholdDays = 5,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Engineer_role_cannot_reach_the_automation_rules_endpoint_at_all()
    {
        await SeedEngineerAsync("automation_rule_engineer@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("automation_rule_engineer@pulse.io");

        var resp = await client.GetAsync("/api/v1/automation-rules");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Scanner_reassigns_a_real_blocked_task_to_the_team_lead()
    {
        var lead = await SeedEngineerAsync("automation_rule_scan_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Automation Rule Scan Team", lead.Id);
        var engineer = await SeedEngineerAsync("automation_rule_scan_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var project = await SeedProjectAsync("Automation Rule Scan Project");

        var task = await SeedTaskAsync("Stuck task", project.Id, assigneeId: engineer.Id);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var t = await db.Tasks.FirstAsync(x => x.Id == task.Id);
            t.FlagBlocker("stuck for real", lead.Id);
            // Back-date ActivatedAt past the threshold — EF Core can write a private-setter property
            // directly via the change tracker even though the domain has no public setter for it.
            db.Entry(t).Property("ActivatedAt").CurrentValue = DateTime.UtcNow.AddDays(-6);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("automation_rule_scan_lead@pulse.io");
        var created = await client.PostAsJsonAsync("/api/v1/automation-rules", new
        {
            name = "Scan test automation",
            teamId = team.Id,
            thresholdDays = 5,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var scope = Factory.Services.CreateScope())
        {
            var scanner = scope.ServiceProvider.GetRequiredService<AutomationRuleScanner>();
            await scanner.RunAsync();
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var reassigned = await db.Tasks.FirstAsync(x => x.Id == task.Id);
            reassigned.AssigneeId.Should().Be(lead.Id);

            var execution = await db.AutomationExecutions.FirstOrDefaultAsync(e => e.TaskId == task.Id);
            execution.Should().NotBeNull();
        }
    }
}
