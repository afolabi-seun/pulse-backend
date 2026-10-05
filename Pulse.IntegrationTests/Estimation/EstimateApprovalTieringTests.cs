using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Estimation;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Estimation;

[Collection("Integration")]
public class EstimateApprovalTieringTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public EstimateApprovalTieringTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task RunEscalationScannerAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<EstimateApprovalEscalationScanner>();
        await scanner.RunAsync();
    }

    private async Task BackdateSubmittedAtAsync(Guid taskId, DateTime submittedAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_estimation_sessions SET submitted_at = {submittedAt} WHERE task_id = {taskId}");
    }

    [Fact]
    public async Task Engineer_can_submit_their_own_estimate_and_their_team_lead_can_approve_it()
    {
        var lead = await SeedEngineerAsync("tier_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Tiering Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var engineer = await SeedEngineerAsync("tier_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Tiering Project", team.Id);
        var task = await SeedTaskAsync("Needs estimate", project.Id, points: 0, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("tier_eng@pulse.io");
        var leadClient = await AuthenticatedClientAsync("tier_lead@pulse.io");

        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var submitResponse = await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var leadView = await (await leadClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        leadView!.Data!.CanApprove.Should().BeTrue();
        leadView.Data.IsEscalated.Should().BeFalse();

        var approveResponse = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedTask = await (await leadClient.GetAsync($"/api/v1/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        updatedTask!.Data!.Points.Should().Be(5);
    }

    [Fact]
    public async Task A_team_leads_own_task_estimate_goes_straight_to_the_department_head()
    {
        var lead = await SeedEngineerAsync("tier_selflead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Self Lead Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var head = await SeedEngineerAsync("tier_head1@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var project = await SeedProjectAsync("Self Lead Project", team.Id);
        var task = await SeedTaskAsync("Lead's own task", project.Id, points: 0, assigneeId: lead.Id);

        var leadClient = await AuthenticatedClientAsync("tier_selflead@pulse.io");
        var headClient = await AuthenticatedClientAsync("tier_head1@pulse.io");

        (await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 8 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 8 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var headView = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        headView!.Data!.CanApprove.Should().BeTrue();

        // The lead can't approve their own submitted estimate — there's no tier above a lead's
        // own task other than the department head.
        var leadView = await (await leadClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        leadView!.Data!.CanApprove.Should().BeFalse();
    }

    [Fact]
    public async Task An_engineer_with_no_team_lead_resolves_straight_to_the_department_head()
    {
        var head = await SeedEngineerAsync("tier_head2@pulse.io", Roles.HeadOfRnD);
        var team = await SeedTeamAsync("No Lead Team", teamLeadId: null, department: "Engineering");
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var engineer = await SeedEngineerAsync("tier_eng2@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("No Lead Project", team.Id);
        var task = await SeedTaskAsync("Needs estimate", project.Id, points: 0, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("tier_eng2@pulse.io");
        var headClient = await AuthenticatedClientAsync("tier_head2@pulse.io");

        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await headClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var headView = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        headView!.Data!.CanApprove.Should().BeTrue();
    }

    [Fact]
    public async Task Escalation_scanner_gives_the_department_head_approval_access_without_removing_the_team_leads()
    {
        var lead = await SeedEngineerAsync("tier_esclead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Escalation Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var head = await SeedEngineerAsync("tier_eschead@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var engineer = await SeedEngineerAsync("tier_esceng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Escalation Project", team.Id);
        var task = await SeedTaskAsync("Needs estimate", project.Id, points: 0, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("tier_esceng@pulse.io");
        var leadClient = await AuthenticatedClientAsync("tier_esclead@pulse.io");
        var headClient = await AuthenticatedClientAsync("tier_eschead@pulse.io");

        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Before escalation: only the team lead can act.
        var headViewBefore = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        headViewBefore!.Data!.CanApprove.Should().BeFalse();

        await BackdateSubmittedAtAsync(task.Id, DateTime.UtcNow.AddHours(-25));
        await RunEscalationScannerAsync();

        var headViewAfter = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        headViewAfter!.Data!.CanApprove.Should().BeTrue();
        headViewAfter.Data.IsEscalated.Should().BeTrue();

        var leadViewAfter = await (await leadClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        leadViewAfter!.Data!.CanApprove.Should().BeTrue("escalation adds the head, it doesn't revoke the team lead");
    }

    [Fact]
    public async Task A_team_lead_who_submits_for_their_engineer_cannot_approve_or_reject_it_and_the_head_can()
    {
        var lead = await SeedEngineerAsync("tier_submitlead@cadence.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Submit Lead Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var head = await SeedEngineerAsync("tier_submithead@cadence.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var engineer = await SeedEngineerAsync("tier_submiteng@cadence.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Submit Lead Project", team.Id);
        var task = await SeedTaskAsync("Lead submits for engineer", project.Id, points: 0, assigneeId: engineer.Id);

        var leadClient = await AuthenticatedClientAsync("tier_submitlead@cadence.io");
        var headClient = await AuthenticatedClientAsync("tier_submithead@cadence.io");

        (await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        // The lead submits on the engineer's behalf — so the lead is the submitter, and the engineer's own first-line approver.
        (await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var leadView = await (await leadClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        leadView!.Data!.CanApprove.Should().BeFalse();
        var headView = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}/estimation"))
            .Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts);
        headView!.Data!.CanApprove.Should().BeTrue("with the submitter out of the way the request goes to the department head");

        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/reject", new { reason = "mine" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await headClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await (await headClient.GetAsync($"/api/v1/tasks/{task.Id}")).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        updated!.Data!.Points.Should().Be(5);
    }
}
