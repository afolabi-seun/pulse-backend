using Pulse.Application.Automations.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Automations;

public class CreateAutomationRuleHandlerTests
{
    private readonly Mock<IAutomationRuleRepository> _rules = new();
    private readonly Mock<ITeamRepository> _teams = new();

    private CreateAutomationRuleHandler CreateHandler() => new(_rules.Object, _teams.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_the_team_does_not_exist()
    {
        _teams.Setup(t => t.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Team?)null);

        var result = await CreateHandler().Handle(new CreateAutomationRuleCommand(
            "Name", Guid.NewGuid(), 5, Guid.NewGuid(), Roles.ProjectManager), default);

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

        var result = await CreateHandler().Handle(new CreateAutomationRuleCommand(
            "Name", team.Id, 5, actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_the_team_has_no_lead()
    {
        var team = Team.Create("Leadless team", null, "Engineering");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var result = await CreateHandler().Handle(new CreateAutomationRuleCommand(
            "Name", team.Id, 5, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task TeamLead_can_create_a_rule_for_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Led team", actorId, "Engineering");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { team });

        var result = await CreateHandler().Handle(new CreateAutomationRuleCommand(
            "Name", team.Id, 5, actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        _rules.Verify(r => r.AddAsync(It.IsAny<Domain.Automations.AutomationRule>(), default), Times.Once);
    }
}
