using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Reports.Queries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Reports;

public class GetOrgTrendQueryTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();

    public GetOrgTrendQueryTests()
    {
        // No progress has been earned unless a test says so.
        _tasks.Setup(t => t.GetWeeklyEarnedOrgWideAsync(default)).ReturnsAsync(Array.Empty<WeeklyEarnedPoint>());
    }

    private GetOrgTrendHandler CreateHandler() => new(_tasks.Object, _escalationEvents.Object);

    [Fact]
    public async Task Returns_delivery_and_escalation_trends_from_the_repositories()
    {
        var week1 = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var week2 = DateOnly.FromDateTime(DateTime.UtcNow);
        _tasks
            .Setup(t => t.GetWeeklyThroughputOrgWideAsync(default))
            .ReturnsAsync(new[] { new WeeklyThroughputPoint(week1, 20), new WeeklyThroughputPoint(week2, 35) });
        _escalationEvents
            .Setup(e => e.GetWeeklyEscalationCountAsync(default))
            .ReturnsAsync(new[] { new WeeklyEscalationPoint(week1, 3), new WeeklyEscalationPoint(week2, 1) });

        var result = await CreateHandler().Handle(new GetOrgTrendQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.DeliveryTrend.Should().BeEquivalentTo(new[]
        {
            new ThroughputWeekDto(week1, 20),
            new ThroughputWeekDto(week2, 35),
        });
        result.Data!.EscalationTrend.Should().BeEquivalentTo(new[]
        {
            new WeeklyEscalationPoint(week1, 3),
            new WeeklyEscalationPoint(week2, 1),
        });
    }

    [Fact]
    public async Task Returns_empty_trends_when_no_history_exists()
    {
        _tasks.Setup(t => t.GetWeeklyThroughputOrgWideAsync(default)).ReturnsAsync(Array.Empty<WeeklyThroughputPoint>());
        _escalationEvents.Setup(e => e.GetWeeklyEscalationCountAsync(default)).ReturnsAsync(Array.Empty<WeeklyEscalationPoint>());

        var result = await CreateHandler().Handle(new GetOrgTrendQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.DeliveryTrend.Should().BeEmpty();
        result.Data!.EscalationTrend.Should().BeEmpty();
    }

    [Fact]
    public async Task Earned_points_sit_beside_delivered_ones_and_a_week_with_only_earned_points_is_kept()
    {
        var week1 = new DateOnly(2026, 10, 5);
        var week2 = new DateOnly(2026, 10, 12);
        _tasks.Setup(t => t.GetWeeklyThroughputOrgWideAsync(default)).ReturnsAsync(new[] { new WeeklyThroughputPoint(week1, 20) });
        _tasks.Setup(t => t.GetWeeklyEarnedOrgWideAsync(default))
            .ReturnsAsync(new[] { new WeeklyEarnedPoint(week1, 22.5m), new WeeklyEarnedPoint(week2, 4m) });
        _escalationEvents.Setup(e => e.GetWeeklyEscalationCountAsync(default)).ReturnsAsync(Array.Empty<WeeklyEscalationPoint>());

        var result = await CreateHandler().Handle(new GetOrgTrendQuery(), default);

        result.Data!.DeliveryTrend.Should().Equal(new ThroughputWeekDto(week1, 20, 22.5m), new ThroughputWeekDto(week2, 0, 4m));
    }
}
