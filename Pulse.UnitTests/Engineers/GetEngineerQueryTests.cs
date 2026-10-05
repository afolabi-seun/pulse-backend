using Pulse.Application.Common.Interfaces;
using Pulse.Application.Engineers.Queries;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Engineers;

public class GetEngineerQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();

    private GetEngineerHandler CreateHandler() => new(_engineers.Object);

    private static Engineer MakeEngineer(string name = "Target") =>
        Engineer.Create(name, $"{name.ToLower()}@ex.com", "hash", Roles.Engineer, 10, 5);

    [Fact]
    public async Task Self_view_is_always_allowed()
    {
        var self = MakeEngineer();
        _engineers.Setup(r => r.GetByIdAsync(self.Id, default)).ReturnsAsync(self);

        var result = await CreateHandler().Handle(new GetEngineerQuery(self.Id, self.Id, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Pm_or_above_can_view_any_engineer()
    {
        var target = MakeEngineer();
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(target.Id, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Executive_can_view_any_engineer()
    {
        var target = MakeEngineer();
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(target.Id, Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Hr_can_view_any_engineer()
    {
        var target = MakeEngineer();
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(target.Id, Guid.NewGuid(), Roles.HR), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Team_lead_can_view_own_team_member()
    {
        var team = Domain.Teams.Team.Create("Alpha");
        var lead = MakeEngineer("Lead");
        lead.AssignToTeam(team.Id);
        var member = MakeEngineer("Member");
        member.AssignToTeam(team.Id);

        _engineers.Setup(r => r.GetByIdAsync(member.Id, default)).ReturnsAsync(member);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(member.Id, lead.Id, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Team_lead_cannot_view_engineer_outside_own_team()
    {
        var team = Domain.Teams.Team.Create("Alpha");
        var otherTeam = Domain.Teams.Team.Create("Beta");
        var lead = MakeEngineer("Lead");
        lead.AssignToTeam(team.Id);
        var outsider = MakeEngineer("Outsider");
        outsider.AssignToTeam(otherTeam.Id);

        _engineers.Setup(r => r.GetByIdAsync(outsider.Id, default)).ReturnsAsync(outsider);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(outsider.Id, lead.Id, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Engineer_cannot_view_a_colleague()
    {
        var target = MakeEngineer();
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(target.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_not_found_for_missing_engineer()
    {
        _engineers.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Engineer?)null);

        var result = await CreateHandler().Handle(
            new GetEngineerQuery(Guid.NewGuid(), Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }
}
