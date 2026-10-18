using Pulse.Application.Projects.Queries;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Engineers.Queries;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Engineers;

public class GetEngineerThroughputQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITaskRepository> _tasks = new();

    private GetEngineerThroughputHandler CreateHandler() => new(_engineers.Object, _tasks.Object);

    private Engineer SeedTarget()
    {
        var target = Engineer.Create("Target", "target@ex.com", "hash", Roles.Engineer, 10, 5);
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);
        _tasks.Setup(t => t.GetWeeklyThroughputByAssigneeAsync(target.Id, default))
            .ReturnsAsync(new List<WeeklyThroughputPoint>());
        _tasks.Setup(t => t.GetWeeklyEarnedByAssigneeAsync(target.Id, default))
            .ReturnsAsync(new List<WeeklyEarnedPoint>());
        return target;
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Org_read_only_roles_can_view_any_engineers_throughput(string role)
    {
        var target = SeedTarget();

        var result = await CreateHandler().Handle(
            new GetEngineerThroughputQuery(target.Id, Guid.NewGuid(), role), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Another_engineer_is_still_forbidden()
    {
        var target = SeedTarget();

        var result = await CreateHandler().Handle(
            new GetEngineerThroughputQuery(target.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Earned_points_are_returned_beside_delivered_ones_including_weeks_where_nothing_was_finished()
    {
        var target = SeedTarget();
        var week1 = new DateOnly(2026, 10, 5);
        var week2 = new DateOnly(2026, 10, 12);
        _tasks.Setup(t => t.GetWeeklyThroughputByAssigneeAsync(target.Id, default))
            .ReturnsAsync(new List<WeeklyThroughputPoint> { new(week1, 5) });
        _tasks.Setup(t => t.GetWeeklyEarnedByAssigneeAsync(target.Id, default))
            .ReturnsAsync(new List<WeeklyEarnedPoint> { new(week1, 5m), new(week2, 2.5m) });

        var result = await CreateHandler().Handle(new GetEngineerThroughputQuery(target.Id, target.Id, Roles.Engineer), default);

        result.Data.Should().Equal(new ThroughputWeekDto(week1, 5, 5m), new ThroughputWeekDto(week2, 0, 2.5m));
    }
}
