using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Estimation;

public class EstimationApprovalTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private static readonly Guid AnyProject = Guid.NewGuid();

    public EstimationApprovalTests()
    {
        // No PMO unless a test adds one.
        _engineers.Setup(r => r.ListByRoleAsync(It.IsAny<string>(), default)).ReturnsAsync([]);
    }

    private (Engineer Assignee, Engineer TeamLead, Engineer Head, Team Team) SeedTeamWithLeadAndHead()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var teamLead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 20, 14);
        team.SetTeamLead(teamLead.Id);
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(teamLead.Id, default)).ReturnsAsync(teamLead);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, teamLead, head]);

        return (assignee, teamLead, head, team);
    }

    [Fact]
    public async Task ResolveApproversAsync_returns_the_assignees_team_lead_in_the_normal_case()
    {
        var (assignee, teamLead, _, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.TeamLead);
        state.Approvers.Should().ContainSingle(a => a.Id == teamLead.Id);
    }

    [Fact]
    public async Task ResolveApproversAsync_skips_straight_to_the_head_when_the_assignee_is_a_team_lead()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var assigneeWhoLeads = Engineer.Create("Lead-as-assignee", "lead2@x.io", "hash", Roles.TeamLead, 20, 14);
        assigneeWhoLeads.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assigneeWhoLeads.Id, default)).ReturnsAsync(assigneeWhoLeads);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assigneeWhoLeads, head]);

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assigneeWhoLeads.Id, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.DepartmentHead);
        state.Approvers.Should().ContainSingle(a => a.Id == head.Id);
    }

    [Fact]
    public async Task ResolveApproversAsync_goes_straight_to_the_head_when_no_team_lead_is_resolvable()
    {
        var team = Team.Create("Platform"); // no TeamLeadId set
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.DepartmentHead);
        state.Approvers.Should().ContainSingle(a => a.Id == head.Id);
    }

    [Fact]
    public async Task ResolveApproversAsync_once_escalated_returns_both_the_team_lead_and_the_head()
    {
        var (assignee, teamLead, head, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.DepartmentHead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.DepartmentHead);
        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([teamLead.Id, head.Id]);
    }

    [Fact]
    public async Task IsAuthorizedAsync_falls_back_to_team_lead_or_above_when_nobody_resolves()
    {
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14); // no team at all
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee]);

        var authorizedPm = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, Guid.NewGuid(), Roles.ProjectManager, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);
        var authorizedRandomEngineer = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, Guid.NewGuid(), Roles.Engineer, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        authorizedPm.Should().BeTrue();
        authorizedRandomEngineer.Should().BeFalse();
    }

    // ── The submitter never approves their own submission ──────────────────────

    [Fact]
    public async Task A_team_lead_who_submits_for_their_engineer_is_skipped_and_the_head_is_the_approver()
    {
        var (assignee, teamLead, head, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.TeamLead, teamLead.Id, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.DepartmentHead);
        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([head.Id]);
    }

    [Fact]
    public async Task When_the_engineer_submits_their_own_estimate_the_team_lead_is_still_the_first_approver()
    {
        var (assignee, teamLead, _, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.TeamLead, assignee.Id, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.TeamLead);
        state.Approvers.Should().ContainSingle(a => a.Id == teamLead.Id);
    }

    [Fact]
    public async Task A_head_who_submits_is_dropped_from_the_escalated_list()
    {
        var (assignee, teamLead, head, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(AnyProject, assignee.Id, ApprovalStage.DepartmentHead, head.Id, _projects.Object, _engineers.Object, _teams.Object, default);

        state!.Approvers.Select(a => a.Id).Should().BeEquivalentTo([teamLead.Id]);
    }

    [Fact]
    public async Task IsAuthorizedAsync_refuses_the_submitter_even_when_they_would_otherwise_qualify()
    {
        var (assignee, teamLead, _, _) = SeedTeamWithLeadAndHead();

        var asSubmitter = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, teamLead.Id, Roles.TeamLead, ApprovalStage.TeamLead, teamLead.Id, _projects.Object, _engineers.Object, _teams.Object, default);

        asSubmitter.Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_lets_the_head_approve_a_team_leads_submission()
    {
        var (assignee, teamLead, head, _) = SeedTeamWithLeadAndHead();

        var asHead = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, head.Id, Roles.HeadOfRnD, ApprovalStage.TeamLead, teamLead.Id, _projects.Object, _engineers.Object, _teams.Object, default);

        asHead.Should().BeTrue();
    }

    [Fact]
    public async Task The_team_lead_plus_fallback_also_excludes_the_submitter()
    {
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14); // no team: nobody resolves
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee]);
        var submittingPm = Guid.NewGuid();

        var submitter = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, submittingPm, Roles.ProjectManager, ApprovalStage.TeamLead, submittingPm, _projects.Object, _engineers.Object, _teams.Object, default);
        var otherPm = await EstimationApproval.IsAuthorizedAsync(
            AnyProject, assignee.Id, Guid.NewGuid(), Roles.ProjectManager, ApprovalStage.TeamLead, submittingPm, _projects.Object, _engineers.Object, _teams.Object, default);

        submitter.Should().BeFalse();
        otherPm.Should().BeTrue();
    }

    // ── The team and department that OWN the task, and the PMO tier ───────────

    /// <summary>An engineer in Design working on a project owned by Engineering's Platform team — the lead and head that matter are Engineering's.</summary>
    private (Engineer Assignee, Engineer OwnerLead, Engineer OwnerHead, Engineer AssigneeHead, Project Project) SeedLoanedToAnotherDepartment()
    {
        var platform = Team.Create("Platform");
        platform.SetDepartment("Engineering");
        var ownerLead = Engineer.Create("Owner lead", "ol@x.io", "hash", Roles.TeamLead, 20, 14);
        platform.SetTeamLead(ownerLead.Id);
        var ownerHead = Engineer.Create("Owner head", "oh@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        ownerHead.AssignToTeam(platform.Id);

        var design = Team.Create("Design");
        design.SetDepartment("Design");
        var designLead = Engineer.Create("Design lead", "dl@x.io", "hash", Roles.TeamLead, 20, 14);
        design.SetTeamLead(designLead.Id);
        var assigneeHead = Engineer.Create("Design head", "dh@x.io", "hash", Roles.HeadOfDesign, 20, 14);
        assigneeHead.AssignToTeam(design.Id);
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Designer, 20, 14);
        assignee.AssignToTeam(design.Id);

        var project = Project.Create("Proj", ownerTeamId: platform.Id);
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _teams.Setup(r => r.GetByIdAsync(platform.Id, default)).ReturnsAsync(platform);
        _teams.Setup(r => r.GetByIdAsync(design.Id, default)).ReturnsAsync(design);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([platform, design]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(ownerLead.Id, default)).ReturnsAsync(ownerLead);
        _engineers.Setup(r => r.GetByIdAsync(designLead.Id, default)).ReturnsAsync(designLead);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, ownerLead, designLead, ownerHead, assigneeHead]);
        return (assignee, ownerLead, ownerHead, assigneeHead, project);
    }

    [Fact]
    public async Task The_first_approver_is_the_lead_of_the_team_that_owns_the_project_not_the_assignees_own_lead()
    {
        var (assignee, ownerLead, _, _, project) = SeedLoanedToAnotherDepartment();

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, assignee.Id, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Stage.Should().Be(ApprovalStage.TeamLead);
        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([ownerLead.Id]);
    }

    [Fact]
    public async Task The_head_is_the_one_of_the_department_that_owns_the_project_not_the_assignees_department()
    {
        var (assignee, ownerLead, ownerHead, assigneeHead, project) = SeedLoanedToAnotherDepartment();

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, assignee.Id, ApprovalStage.DepartmentHead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([ownerLead.Id, ownerHead.Id]);
        state.Approvers.Should().NotContain(a => a.Id == assigneeHead.Id);
    }

    [Fact]
    public async Task An_unassigned_task_still_resolves_through_the_owning_team()
    {
        var (_, ownerLead, _, _, project) = SeedLoanedToAnotherDepartment();

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, null, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([ownerLead.Id]);
    }

    [Fact]
    public async Task A_head_of_pmo_on_a_team_in_the_department_is_not_that_departments_head()
    {
        var (assignee, _, ownerHead, _, project) = SeedLoanedToAnotherDepartment();
        var pmoHead = Engineer.Create("Pmo head", "ph@x.io", "hash", Roles.HeadOfPmo, 20, 14);
        var platform = (await _teams.Object.ListAllAsync(default)).First(t => t.Department == "Engineering");
        pmoHead.AssignToTeam(platform.Id);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(
            [assignee, ownerHead, pmoHead]);

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, assignee.Id, ApprovalStage.DepartmentHead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Approvers.Should().NotContain(a => a.Id == pmoHead.Id, "the PMO is its own tier, not a department head");
        state.Approvers.Should().Contain(a => a.Id == ownerHead.Id);
    }

    [Fact]
    public async Task Once_escalated_to_the_pmo_everyone_below_stays_and_the_pmo_joins()
    {
        var (assignee, ownerLead, ownerHead, _, project) = SeedLoanedToAnotherDepartment();
        var pm = Engineer.Create("PM", "pm@x.io", "hash", Roles.ProjectManager, 20, 14);
        var pmoHead = Engineer.Create("Pmo head", "ph@x.io", "hash", Roles.HeadOfPmo, 20, 14);
        _engineers.Setup(r => r.ListByRoleAsync(Roles.ProjectManager, default)).ReturnsAsync([pm]);
        _engineers.Setup(r => r.ListByRoleAsync(Roles.HeadOfPmo, default)).ReturnsAsync([pmoHead]);

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, assignee.Id, ApprovalStage.Pmo, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Stage.Should().Be(ApprovalStage.Pmo);
        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([ownerLead.Id, ownerHead.Id, pm.Id, pmoHead.Id]);
    }

    [Fact]
    public async Task With_nobody_below_the_pmo_the_request_goes_straight_to_the_pmo()
    {
        var team = Team.Create("Platform"); // no lead, and no department so no head can be found
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var pm = Engineer.Create("PM", "pm@x.io", "hash", Roles.ProjectManager, 20, 14);
        var project = Project.Create("Proj", ownerTeamId: team.Id);
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee]);
        _engineers.Setup(r => r.ListByRoleAsync(Roles.ProjectManager, default)).ReturnsAsync([pm]);

        var state = await EstimationApproval.ResolveApproversAsync(project.Id, assignee.Id, ApprovalStage.TeamLead, null, _projects.Object, _engineers.Object, _teams.Object, default);

        state.Stage.Should().Be(ApprovalStage.Pmo);
        state.Approvers.Select(a => a.Id).Should().BeEquivalentTo([pm.Id]);
    }
}
