using Pulse.Application.CheckIns;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.CheckIns;

public class AutoCheckInTests
{
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Guid _engineer = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private CheckIn? _today;

    public AutoCheckInTests()
    {
        _checkIns.Setup(c => c.GetByEngineerAndDateAsync(_engineer, It.IsAny<DateOnly>(), _project, default)).ReturnsAsync(() => _today);
        _checkIns.Setup(c => c.AddAsync(It.IsAny<CheckIn>(), default)).Callback<CheckIn, CancellationToken>((c, _) => _today = c).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task A_progress_line_replaces_that_tasks_earlier_line_and_leaves_every_other_line_alone()
    {
        await AutoCheckIn.EnsureForTaskCompletionAsync(_checkIns.Object, _engineer, _project, "Write docs", default);
        await AutoCheckIn.EnsureForProgressAsync(_checkIns.Object, _engineer, _project, "Fix login", 25, default);
        await AutoCheckIn.EnsureForChecklistAsync(_checkIns.Object, _engineer, _project, "Fix login", 1, 4, default);

        await AutoCheckIn.EnsureForProgressAsync(_checkIns.Object, _engineer, _project, "Fix login", 75, default);

        _today!.Completed.Split('\n').Should().Equal(
            "Completed: Write docs", "Progress: Fix login — 75%", "Checklist: Fix login — 1 of 4 items done");
    }

    [Fact]
    public async Task Progress_and_checklist_lines_for_the_same_task_are_kept_separate()
    {
        await AutoCheckIn.EnsureForProgressAsync(_checkIns.Object, _engineer, _project, "Fix login", 50, default);
        await AutoCheckIn.EnsureForChecklistAsync(_checkIns.Object, _engineer, _project, "Fix login", 2, 5, default);
        await AutoCheckIn.EnsureForChecklistAsync(_checkIns.Object, _engineer, _project, "Fix login", 3, 5, default);

        _today!.Completed.Split('\n').Should().Equal("Progress: Fix login — 50%", "Checklist: Fix login — 3 of 5 items done");
    }

    [Fact]
    public async Task Two_tasks_get_two_lines()
    {
        await AutoCheckIn.EnsureForProgressAsync(_checkIns.Object, _engineer, _project, "Fix login", 50, default);
        await AutoCheckIn.EnsureForProgressAsync(_checkIns.Object, _engineer, _project, "Fix signup", 25, default);

        _today!.Completed.Split('\n').Should().Equal("Progress: Fix login — 50%", "Progress: Fix signup — 25%");
    }
}
