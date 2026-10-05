using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class GetEngineerTimeActivityQueryTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();

    private GetEngineerTimeActivityHandler CreateHandler() =>
        new(_timeEntries.Object, _engineers.Object, _teams.Object, _tasks.Object, _projects.Object);

    private static readonly DateOnly From = new(2026, 9, 28);
    private static readonly DateOnly To = new(2026, 10, 4);

    /// <summary>The engineer logged 2h on a task the caller cannot load — row-level security hides personal
    /// tasks from Executive/HR/Accountant, so the task lookup comes back empty.</summary>
    private Guid SeedHiddenTaskTime(Guid engineerId)
    {
        var hiddenTaskId = Guid.NewGuid();
        _timeEntries.Setup(r => r.GetByEngineerAndDateRangeAsync(engineerId, From, To, default))
            .ReturnsAsync(new List<TimeEntry>
            {
                TimeEntry.Log(engineerId, From, TimeEntryCategory.Task, hiddenTaskId, null, 2m, null),
            });
        _tasks.Setup(t => t.GetActiveByAssigneeAsync(engineerId, default)).ReturnsAsync(new List<PulseTask>());
        _tasks.Setup(t => t.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new List<PulseTask>());
        _projects.Setup(p => p.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(p => p.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new HashSet<Guid>());
        return hiddenTaskId;
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Hidden_personal_task_shows_as_Personal_task_among_assigned_for_org_read_only_roles(string role)
    {
        var engineerId = Guid.NewGuid();
        var taskId = SeedHiddenTaskTime(engineerId);

        var result = await CreateHandler().Handle(
            new GetEngineerTimeActivityQuery(Guid.NewGuid(), role, engineerId, From, To), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.LoggedOnly.Should().BeEmpty();
        var row = result.Data.Assigned.Should().ContainSingle().Subject;
        row.TaskId.Should().Be(taskId);
        row.Title.Should().Be("Personal task");
        row.Hours.Should().Be(2m);
    }

    [Fact]
    public async Task Hidden_task_for_a_non_read_only_role_still_shows_as_an_unnamed_logged_task()
    {
        var engineerId = Guid.NewGuid();
        SeedHiddenTaskTime(engineerId);

        var result = await CreateHandler().Handle(
            new GetEngineerTimeActivityQuery(Guid.NewGuid(), Roles.HeadOfPmo, engineerId, From, To), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Assigned.Should().BeEmpty();
        result.Data.LoggedOnly.Should().ContainSingle().Which.Title.Should().Be("Private task");
    }
}
