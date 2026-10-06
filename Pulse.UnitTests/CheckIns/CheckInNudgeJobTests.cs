using Pulse.Application.CheckIns;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.CheckIns;

public class CheckInNudgeJobTests
{
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    private CheckInNudgeJob CreateJob() => new(_checkIns.Object, _engineers.Object, _settings.Object, TestNotifications.Dispatcher(email: _emailQueue));

    private static Engineer NewEngineer(string name) =>
        Engineer.Create(name, $"{name.ToLower()}@test.io", "hash", Roles.Engineer, 20, 14);

    [Fact]
    public async Task Also_missed_yesterday_is_true_when_no_row_at_all_exists_for_yesterday()
    {
        var engineer = NewEngineer("Dev");
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _checkIns.Setup(c => c.GetEngineersWithoutCheckInTodayAsync(default)).ReturnsAsync(new[] { engineer.Id });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>?>(ids => ids != null && ids.SequenceEqual(new[] { engineer.Id })), default))
            .ReturnsAsync(Array.Empty<CheckIn>());

        await CreateJob().RunAsync();

        _emailQueue.Verify(q => q.Enqueue(engineer.Email,
            It.Is<string>(s => s.Contains("quick check-in")), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Also_missed_yesterday_is_false_when_a_project_scoped_auto_checkin_exists_for_yesterday()
    {
        // Regression: this must check for ANY row on that date, not a project-less one specifically —
        // AutoCheckIn (Pulse.Application/CheckIns/AutoCheckIn.cs) always tags a real ProjectId, so an
        // engineer who only auto-checked-in via a completed task yesterday has no project-less row at
        // all, and must not be wrongly treated as having missed yesterday too.
        var engineer = NewEngineer("Dev");
        var yesterdaysCheckIn = CheckIn.Submit(engineer.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            "Completed: Something", "", null, Guid.NewGuid());
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _checkIns.Setup(c => c.GetEngineersWithoutCheckInTodayAsync(default)).ReturnsAsync(new[] { engineer.Id });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>?>(ids => ids != null && ids.SequenceEqual(new[] { engineer.Id })), default))
            .ReturnsAsync(new[] { yesterdaysCheckIn });

        await CreateJob().RunAsync();

        _emailQueue.Verify(q => q.Enqueue(engineer.Email,
            It.Is<string>(s => s.Contains("end-of-day check-in nudge")), It.IsAny<string>()), Times.Once);
    }
}
