using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class StopTimerCommandTests
{
    private readonly Mock<IActiveTimerRepository> _timers = new();
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private StopTimerHandler CreateHandler() => new(_timers.Object, _timeEntries.Object, _audit.Object);

    [Fact]
    public async Task Fails_with_not_found_when_no_timer_is_running()
    {
        var engineerId = Guid.NewGuid();
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync((ActiveTimer?)null);

        var result = await CreateHandler().Handle(new StopTimerCommand(engineerId, engineerId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Stops_a_running_meeting_timer_and_logs_a_time_entry()
    {
        var engineerId = Guid.NewGuid();
        var timer = ActiveTimer.Start(engineerId, TimeEntryCategory.Meeting, null);
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync(timer);

        var result = await CreateHandler().Handle(new StopTimerCommand(engineerId, engineerId, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Category.Should().Be("meeting");
        result.Data!.Hours.Should().BeGreaterThan(0);
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Once);
        _timers.Verify(r => r.DeleteAsync(timer, default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_TIMER_STOPPED", engineerId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Clamps_hours_to_24_for_a_stale_timer()
    {
        var engineerId = Guid.NewGuid();
        var timer = ActiveTimer.Start(engineerId, TimeEntryCategory.Meeting, null);
        typeof(ActiveTimer).GetProperty("StartedAt")!.SetValue(timer, DateTime.UtcNow.AddDays(-3));
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync(timer);

        var result = await CreateHandler().Handle(new StopTimerCommand(engineerId, engineerId, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Hours.Should().Be(24);
    }

    [Fact]
    public async Task Floors_hours_at_0_01_for_a_just_started_timer()
    {
        var engineerId = Guid.NewGuid();
        var timer = ActiveTimer.Start(engineerId, TimeEntryCategory.Meeting, null);
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync(timer);

        var result = await CreateHandler().Handle(new StopTimerCommand(engineerId, engineerId, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Hours.Should().BeGreaterThanOrEqualTo(0.01m);
    }
}
