using Pulse.Application.Common.Interfaces;
using Pulse.Application.Teams.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Teams;

public class ListTeamsHandlerTests
{
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IEngineerRepository> _engineers = new();

    private ListTeamsHandler CreateHandler() => new(_teams.Object, _engineers.Object);

    [Fact]
    public async Task Global_role_sees_headcount_and_lead_for_every_team()
    {
        var ownTeam   = Team.Create("R&D A", null, "R&D");
        var otherTeam = Team.Create("Design A", null, "Design");
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync(new[] { ownTeam, otherTeam });
        _engineers.Setup(r => r.CountByTeamAsync(default))
            .ReturnsAsync(new Dictionary<Guid, int> { [ownTeam.Id] = 3, [otherTeam.Id] = 5 });

        var result = await CreateHandler().Handle(new ListTeamsQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.Data.Should().Contain(t => t.Id == otherTeam.Id && t.MemberCount == 5);
    }

    [Fact]
    public async Task Department_head_sees_headcount_and_lead_for_their_own_department_only()
    {
        var callerId = Guid.NewGuid();
        var ownTeam   = Team.Create("R&D A", null, "R&D");
        var otherTeam = Team.Create("Design A", null, "Design");
        var caller = Engineer.Create("Head", "head@pulse.io", "hash", Roles.HeadOfRnD, 20, 14);
        caller.AssignToTeam(ownTeam.Id);

        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync(new[] { ownTeam, otherTeam });
        _engineers.Setup(r => r.GetByIdAsync(callerId, default)).ReturnsAsync(caller);
        _engineers.Setup(r => r.CountByTeamAsync(default))
            .ReturnsAsync(new Dictionary<Guid, int> { [ownTeam.Id] = 3, [otherTeam.Id] = 5 });

        var result = await CreateHandler().Handle(new ListTeamsQuery(Roles.HeadOfRnD, callerId), default);

        var own   = result.Data!.Single(t => t.Id == ownTeam.Id);
        var other = result.Data!.Single(t => t.Id == otherTeam.Id);

        own.MemberCount.Should().Be(3);
        other.MemberCount.Should().BeNull();
        other.TeamLeadName.Should().BeNull();
    }

    [Fact]
    public async Task Every_team_is_still_returned_regardless_of_department()
    {
        var callerId = Guid.NewGuid();
        var ownTeam   = Team.Create("R&D A", null, "R&D");
        var otherTeam = Team.Create("Design A", null, "Design");
        var caller = Engineer.Create("Head", "head@pulse.io", "hash", Roles.HeadOfRnD, 20, 14);
        caller.AssignToTeam(ownTeam.Id);

        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync(new[] { ownTeam, otherTeam });
        _engineers.Setup(r => r.GetByIdAsync(callerId, default)).ReturnsAsync(caller);
        _engineers.Setup(r => r.CountByTeamAsync(default)).ReturnsAsync(new Dictionary<Guid, int>());

        var result = await CreateHandler().Handle(new ListTeamsQuery(Roles.HeadOfRnD, callerId), default);

        result.Data.Should().HaveCount(2);
        result.Data.Should().Contain(t => t.Id == otherTeam.Id);
    }
}
