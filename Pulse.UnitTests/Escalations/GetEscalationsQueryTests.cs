using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations.Queries;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Escalations;

public class GetEscalationsQueryTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly OverworkThresholds _thresholds = new();

    public GetEscalationsQueryTests()
    {
        _projects.Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(r => r.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
    }

    private GetEscalationsHandler CreateHandler() => new(_tasks.Object, _engineers.Object, _teams.Object, _thresholds, _projects.Object);

    private static PulseTask OverdueTask()
    {
        // GetEscalationCandidatesAsync (mocked here) never returns Backlog tasks for real —
        // give the fixture a real assignee so it matches what the repository actually returns.
        // Assign with a safe future due date first, then set the real (past) one afterward:
        // PromoteFromBacklogIfGroomed corrects a due date that lapsed while still in Backlog,
        // which would otherwise mask this fixture's whole point.
        var task = PulseTask.Create("Overdue task", 3, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria, task.Points, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Guid.NewGuid());
        // ActivatedAt pushed back beyond the reactivation grace window — this fixture represents a
        // task that's genuinely been overdue a while, not one that just reactivated moments ago.
        typeof(PulseTask).GetProperty("ActivatedAt")!.SetValue(task, DateTime.UtcNow.AddDays(-5));
        return task;
    }

    [Fact]
    public async Task Level_is_serialized_as_TMinus_prefixed_not_hyphenated()
    {
        // Regression test: the handler used to emit "T-3"/"T-1", but the frontend (EscalationDto.level:
        // EscalationLevel) has always expected "TMinus3"/"TMinus1"/"Overdue" — the mismatch meant the
        // T-3/T-1 cards on the Escalations dashboard never rendered, only Overdue ever matched.
        var task = OverdueTask();
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        var result = await CreateHandler().Handle(new GetEscalationsQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle().Which.Level.Should().Be("Overdue");
    }

    [Fact]
    public async Task Excludes_tasks_that_are_InQa()
    {
        var task = OverdueTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        var result = await CreateHandler().Handle(new GetEscalationsQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Excludes_tasks_that_are_Backlog()
    {
        // No assignee — the repository query already excludes these, but the handler defends
        // against it too, same as InQa/Paused.
        var task = PulseTask.Create("Unassigned overdue task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        var result = await CreateHandler().Handle(new GetEscalationsQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Excludes_tasks_that_are_Paused()
    {
        var task = OverdueTask();
        task.Pause("note", Guid.NewGuid());

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        var result = await CreateHandler().Handle(new GetEscalationsQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Excludes_tasks_below_the_escalation_threshold()
    {
        // Just activated, due in 20 days — well below both T-3 and T-1 thresholds. The old
        // days-until-due bucketing would have miscategorized this as "T-3"; the shared calculator
        // correctly excludes it.
        var task = PulseTask.Create("On track", 3, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)));
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        var result = await CreateHandler().Handle(new GetEscalationsQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }
}
