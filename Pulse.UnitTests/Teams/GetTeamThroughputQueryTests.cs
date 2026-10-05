using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Teams.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Teams;

public class GetTeamThroughputQueryTests
{
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    private GetTeamThroughputHandler CreateHandler() =>
        new(_teams.Object, _tasks.Object, _access.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_team_missing()
    {
        _teams.Setup(t => t.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Team?)null);

        var result = await CreateHandler().Handle(
            new GetTeamThroughputQuery(Guid.NewGuid(), Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_cannot_access_team()
    {
        var team = Team.Create("Platform");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _access
            .Setup(a => a.CanAccessTeamAsync(team.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new GetTeamThroughputQuery(team.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_weekly_points_from_the_repository()
    {
        var team = Team.Create("Platform");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _access
            .Setup(a => a.CanAccessTeamAsync(team.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var week1 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var week2 = DateOnly.FromDateTime(DateTime.UtcNow);
        _tasks
            .Setup(t => t.GetWeeklyThroughputByTeamAsync(team.Id, default))
            .ReturnsAsync(new[]
            {
                new WeeklyThroughputPoint(week1, 8),
                new WeeklyThroughputPoint(week2, 13),
            });

        var result = await CreateHandler().Handle(
            new GetTeamThroughputQuery(team.Id, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEquivalentTo(new[]
        {
            new ThroughputWeekDto(week1, 8),
            new ThroughputWeekDto(week2, 13),
        });
    }

    [Fact]
    public async Task Executive_bypasses_CanAccessTeamAsync_entirely()
    {
        var team = Team.Create("Platform");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _access
            .Setup(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _tasks
            .Setup(t => t.GetWeeklyThroughputByTeamAsync(team.Id, default))
            .ReturnsAsync(Array.Empty<WeeklyThroughputPoint>());

        var result = await CreateHandler().Handle(
            new GetTeamThroughputQuery(team.Id, Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hr_bypasses_CanAccessTeamAsync_entirely()
    {
        var team = Team.Create("Platform");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _access
            .Setup(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _tasks
            .Setup(t => t.GetWeeklyThroughputByTeamAsync(team.Id, default))
            .ReturnsAsync(Array.Empty<WeeklyThroughputPoint>());

        var result = await CreateHandler().Handle(
            new GetTeamThroughputQuery(team.Id, Guid.NewGuid(), Roles.HR), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
