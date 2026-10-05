using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Estimation;

public class EstimationApprovalTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();

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

        var state = await EstimationApproval.ResolveApproversAsync(assignee.Id, escalatedToHead: false, _engineers.Object, _teams.Object, default);

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

        var state = await EstimationApproval.ResolveApproversAsync(assigneeWhoLeads.Id, escalatedToHead: false, _engineers.Object, _teams.Object, default);

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

        var state = await EstimationApproval.ResolveApproversAsync(assignee.Id, escalatedToHead: false, _engineers.Object, _teams.Object, default);

        state!.Stage.Should().Be(ApprovalStage.DepartmentHead);
        state.Approvers.Should().ContainSingle(a => a.Id == head.Id);
    }

    [Fact]
    public async Task ResolveApproversAsync_once_escalated_returns_both_the_team_lead_and_the_head()
    {
        var (assignee, teamLead, head, _) = SeedTeamWithLeadAndHead();

        var state = await EstimationApproval.ResolveApproversAsync(assignee.Id, escalatedToHead: true, _engineers.Object, _teams.Object, default);

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
            assignee.Id, Guid.NewGuid(), Roles.ProjectManager, escalatedToHead: false, _engineers.Object, _teams.Object, default);
        var authorizedRandomEngineer = await EstimationApproval.IsAuthorizedAsync(
            assignee.Id, Guid.NewGuid(), Roles.Engineer, escalatedToHead: false, _engineers.Object, _teams.Object, default);

        authorizedPm.Should().BeTrue();
        authorizedRandomEngineer.Should().BeFalse();
    }
}
