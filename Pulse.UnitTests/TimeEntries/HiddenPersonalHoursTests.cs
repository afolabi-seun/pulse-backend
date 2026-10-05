using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

/// <summary>Executive/HR/Accountant are filtered out of personal tasks by row-level security, so hours on one
/// arrive with no task attached and no project. For those roles — and only those — they belong to the Personal
/// tasks line rather than General.</summary>
public class HiddenPersonalHoursTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();

    private static readonly DateOnly Monday = new(2026, 9, 28);
    private static readonly DateOnly Sunday = new(2026, 10, 4);

    private readonly Engineer _owner = Engineer.Create("Hannah", "h@x.io", "hash", Roles.HR, 5, 5);
    private readonly Guid _hiddenTaskId = Guid.NewGuid();

    public HiddenPersonalHoursTests()
    {
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([_owner]);
        // 2h on a task the caller can't load + 1h of meetings: both land under "no project" (Guid.Empty).
        var hiddenTime = TimeEntry.Log(_owner.Id, Monday, TimeEntryCategory.Task, _hiddenTaskId, null, 2m, null);
        var meeting = TimeEntry.Log(_owner.Id, Monday, TimeEntryCategory.Meeting, null, null, 1m, null);
        _timeEntries.Setup(r => r.GetEntriesByProjectInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), null, default))
            .ReturnsAsync(new List<(TimeEntry, PulseTask?)> { (hiddenTime, null), (meeting, null) });
        _timeEntries.Setup(r => r.GetHoursByProjectInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [Guid.Empty] = 3m });
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _timeEntries.Setup(r => r.GetDailyHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<(Guid, DateOnly), decimal>());
        _projects.Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(r => r.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(r => r.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new HashSet<Guid>());
    }

    private Task<Pulse.Application.Common.ServiceResult<TimeEntrySummaryDto>> Summary(string role) =>
        new GetTimeEntrySummaryHandler(_timeEntries.Object, _engineers.Object, _teams.Object, _projects.Object)
            .Handle(new GetTimeEntrySummaryQuery(Monday, role, Guid.NewGuid()), default);

    private Task<Pulse.Application.Common.ServiceResult<ProjectTimeActivityDto>> Activity(string role, string kind)
    {
        // The project-kind rows for "no project" come back with the project filter set to null.
        _timeEntries.Setup(r => r.GetEntriesByProjectInRangeAsync(
                Monday, Sunday, It.IsAny<IReadOnlyList<Guid>>(), It.Is<IReadOnlyList<Guid>?>(p => p == null || p.Count == 0), default))
            .ReturnsAsync(new List<(TimeEntry, PulseTask?)>
            {
                (TimeEntry.Log(_owner.Id, Monday, TimeEntryCategory.Task, _hiddenTaskId, null, 2m, null), null),
                (TimeEntry.Log(_owner.Id, Monday, TimeEntryCategory.Meeting, null, null, 1m, null), null),
            });
        return new GetProjectTimeActivityHandler(_timeEntries.Object, _engineers.Object, _teams.Object, _projects.Object)
            .Handle(new GetProjectTimeActivityQuery(Guid.NewGuid(), role, kind, null, Monday, Sunday), default);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Summary_moves_hidden_task_hours_from_General_to_Personal_tasks(string role)
    {
        var result = await Summary(role);

        result.IsSuccess.Should().BeTrue();
        var lines = result.Data!.Projects;
        lines.Should().ContainSingle(p => p.Kind == "general").Which.TotalHours.Should().Be(1m);
        lines.Should().ContainSingle(p => p.Kind == "personal").Which.TotalHours.Should().Be(2m);
    }

    [Fact]
    public async Task Summary_leaves_General_alone_for_a_role_that_can_see_every_task()
    {
        var result = await Summary(Roles.HeadOfPmo);

        result.Data!.Projects.Should().ContainSingle(p => p.Kind == "general").Which.TotalHours.Should().Be(3m);
        result.Data.Projects.Should().NotContain(p => p.Kind == "personal");
    }

    [Fact]
    public async Task Summary_drops_the_General_line_when_it_was_only_hidden_personal_time()
    {
        _timeEntries.Setup(r => r.GetHoursByProjectInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [Guid.Empty] = 2m });
        _timeEntries.Setup(r => r.GetEntriesByProjectInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), null, default))
            .ReturnsAsync(new List<(TimeEntry, PulseTask?)>
            {
                (TimeEntry.Log(_owner.Id, Monday, TimeEntryCategory.Task, _hiddenTaskId, null, 2m, null), null),
            });

        var result = await Summary(Roles.Executive);

        result.Data!.Projects.Should().NotContain(p => p.Kind == "general");
        result.Data.Projects.Should().ContainSingle(p => p.Kind == "personal").Which.TotalHours.Should().Be(2m);
    }

    [Fact]
    public async Task Personal_drill_down_lists_the_owner_by_hours_only_for_a_read_only_role()
    {
        var result = await Activity(Roles.Executive, "personal");

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().BeEmpty("the personal line names people, never tasks");
        result.Data.People.Should().ContainSingle().Which.Hours.Should().Be(2m);
    }

    [Fact]
    public async Task General_drill_down_no_longer_lists_the_hidden_task_for_a_read_only_role()
    {
        var result = await Activity(Roles.Executive, "general");

        result.Data!.TotalHours.Should().Be(1m);
        result.Data.Items.Should().ContainSingle().Which.Label.Should().Be("Meetings");
    }
}
