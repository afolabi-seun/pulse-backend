using Pulse.Application.Alerts.Commands;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class CreateAlertRuleHandlerTests
{
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    private CreateAlertRuleHandler CreateHandler() =>
        new(_rules.Object, _teams.Object, _projects.Object, _access.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_the_team_does_not_exist()
    {
        _teams.Setup(t => t.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Team?)null);

        var result = await CreateHandler().Handle(new CreateAlertRuleCommand(
            "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task TeamLead_cannot_create_a_rule_for_a_team_they_do_not_lead()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Other team", Guid.NewGuid(), "Engineering");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { team });

        var result = await CreateHandler().Handle(new CreateAlertRuleCommand(
            "Name", AlertMetric.BlockerCount, AlertScopeType.Team, team.Id,
            AlertComparator.GreaterThan, 5, true, false, actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task TeamLead_can_create_a_rule_for_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Led team", actorId, "Engineering");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { team });

        var result = await CreateHandler().Handle(new CreateAlertRuleCommand(
            "Name", AlertMetric.BlockerCount, AlertScopeType.Team, team.Id,
            AlertComparator.GreaterThan, 5, true, false, actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        _rules.Verify(r => r.AddAsync(It.IsAny<AlertRule>(), default), Times.Once);
    }

    [Fact]
    public async Task Returns_FORBIDDEN_for_a_Project_scope_the_actor_cannot_access()
    {
        var project = Domain.Projects.Project.Create("Some project");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access
            .Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(new CreateAlertRuleCommand(
            "Name", AlertMetric.QaRejectRate, AlertScopeType.Project, project.Id,
            AlertComparator.GreaterThan, 20, true, false, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_the_metric_does_not_fit_the_scope()
    {
        var project = Domain.Projects.Project.Create("Some project");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access
            .Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateHandler().Handle(new CreateAlertRuleCommand(
            "Name", AlertMetric.TeamVelocity, AlertScopeType.Project, project.Id,
            AlertComparator.GreaterThan, 20, true, false, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }
}
