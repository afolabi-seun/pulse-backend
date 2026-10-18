using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Estimation;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.BackgroundJobs;
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

    private Task RunEscalationScannerAsync() => RunJobAsync<EstimateApprovalEscalationScanner>();

    private async Task RunJobAsync<TJob>() where TJob : Pulse.Application.Common.Interfaces.IRecurringJob
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrganizationJobRunner<TJob>>()
            .RunForOrganizationAsync(Pulse.Domain.Organizations.Organization.DefaultId, CancellationToken.None);
    }

    private async Task BackdateSubmittedAtAsync(Guid taskId, DateTime submittedAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_estimation_sessions SET submitted_at = {submittedAt} WHERE task_id = {taskId}");
    }

    private async Task BackdateEscalatedToHeadAtAsync(Guid taskId, DateTime at)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_estimation_sessions SET escalated_to_head_at = {at} WHERE task_id = {taskId}");
    }

    private async Task<EstimationDto> EstimationAsync(HttpClient client, Guid taskId) =>
        (await (await client.GetAsync($"/api/v1/tasks/{taskId}/estimation")).Content.ReadFromJsonAsync<ApiResponse<EstimationDto>>(JsonOpts))!.Data!;

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

        await BackdateSubmittedAtAsync(task.Id, DateTime.UtcNow.AddDays(-10)); // always spans working days, whatever day the suite runs
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

    [Fact]
    public async Task The_request_goes_to_the_lead_and_head_of_the_team_that_owns_the_project_not_the_assignees_own()
    {
        var ownerLead = await SeedEngineerAsync("own_lead@pulse.io", Roles.TeamLead);
        var ownerTeam = await SeedTeamAsync("Owner Team", ownerLead.Id, "Engineering");
        await AssignEngineerToTeamAsync(ownerLead.Id, ownerTeam.Id);
        var ownerHead = await SeedEngineerAsync("own_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(ownerHead.Id, ownerTeam.Id);

        var homeLead = await SeedEngineerAsync("home_lead@pulse.io", Roles.TeamLead);
        var homeTeam = await SeedTeamAsync("Home Team", homeLead.Id, "Design");
        await AssignEngineerToTeamAsync(homeLead.Id, homeTeam.Id);
        var homeHead = await SeedEngineerAsync("home_head@pulse.io", Roles.HeadOfDesign);
        await AssignEngineerToTeamAsync(homeHead.Id, homeTeam.Id);
        var designer = await SeedEngineerAsync("home_designer@pulse.io", Roles.Designer);
        await AssignEngineerToTeamAsync(designer.Id, homeTeam.Id);

        var project = await SeedProjectAsync("Owned by Engineering", ownerTeam.Id);
        var task = await SeedTaskAsync("Designer on an engineering project", project.Id, points: 0, assigneeId: designer.Id);

        var designerClient = await AuthenticatedClientAsync("home_designer@pulse.io");
        var ownerLeadClient = await AuthenticatedClientAsync("own_lead@pulse.io");
        var ownerHeadClient = await AuthenticatedClientAsync("own_head@pulse.io");
        var homeLeadClient = await AuthenticatedClientAsync("home_lead@pulse.io");
        var homeHeadClient = await AuthenticatedClientAsync("home_head@pulse.io");

        (await designerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ownerLeadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await designerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await EstimationAsync(ownerLeadClient, task.Id);
        view.CanApprove.Should().BeTrue("the owning team's lead is the first approver");
        view.PendingApproverNames.Should().BeEquivalentTo([ownerLead.Name], "the request names who it is waiting on");
        (await EstimationAsync(homeLeadClient, task.Id)).CanApprove.Should().BeFalse("the assignee's own team lead is not the one the project answers to");
        (await EstimationAsync(homeHeadClient, task.Id)).CanApprove.Should().BeFalse("nor is the assignee's own department head");
        (await EstimationAsync(ownerHeadClient, task.Id)).CanApprove.Should().BeFalse("the owning head only joins after the lead has had a working hour");
    }

    [Fact]
    public async Task With_no_response_the_request_widens_to_the_head_and_then_to_the_pmo_and_everyone_keeps_access()
    {
        var lead = await SeedEngineerAsync("pmo_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Pmo Tier Team", lead.Id, "Engineering");
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var head = await SeedEngineerAsync("pmo_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        await SeedEngineerAsync("pmo_pm@pulse.io", Roles.ProjectManager);
        var engineer = await SeedEngineerAsync("pmo_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Pmo Tier Project", team.Id);
        var task = await SeedTaskAsync("Nobody answers", project.Id, points: 0, assigneeId: engineer.Id);

        var engineerClient = await AuthenticatedClientAsync("pmo_eng@pulse.io");
        var leadClient = await AuthenticatedClientAsync("pmo_lead@pulse.io");
        var headClient = await AuthenticatedClientAsync("pmo_head@pulse.io");
        var pmClient = await AuthenticatedClientAsync("pmo_pm@pulse.io");

        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/vote", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/reveal", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/estimation/submit-for-approval", new { points = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Two waits of a working hour each, run as the scheduler would: one tier per pass.
        await BackdateSubmittedAtAsync(task.Id, DateTime.UtcNow.AddDays(-10));
        await RunEscalationScannerAsync();
        (await EstimationAsync(pmClient, task.Id)).CanApprove.Should().BeFalse("the head has been added, not yet the PMO");

        await BackdateEscalatedToHeadAtAsync(task.Id, DateTime.UtcNow.AddDays(-10));
        await RunEscalationScannerAsync();

        var asPm = await EstimationAsync(pmClient, task.Id);
        asPm.CanApprove.Should().BeTrue();
        asPm.IsEscalatedToPmo.Should().BeTrue();
        (await EstimationAsync(headClient, task.Id)).CanApprove.Should().BeTrue();
        (await EstimationAsync(leadClient, task.Id)).CanApprove.Should().BeTrue();

        (await pmClient.PostAsync($"/api/v1/tasks/{task.Id}/estimation/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
