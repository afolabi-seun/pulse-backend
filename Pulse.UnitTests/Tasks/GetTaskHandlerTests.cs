using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class GetTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public GetTaskHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _access
            .Setup(a => a.CanViewTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _subtasks
            .Setup(s => s.GetByTaskIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subtask>());
    }

    private GetTaskHandler CreateHandler() =>
        new(_tasks.Object, _projects.Object, _engineers.Object, _teams.Object, _subtasks.Object, _access.Object);

    private (PulseTask Parent, PulseTask Qa) InQaPair(Guid? qaAssigneeId)
    {
        var parent = PulseTask.Create("Original task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        parent.SetRequiresQa(true);
        parent.SendToQa(Guid.NewGuid());

        var qa = PulseTask.Create("[QA] Original task", 3, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        qa.SetParentTaskId(parent.Id);
        if (qaAssigneeId.HasValue)
            qa.Assign(qaAssigneeId.Value, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        return (parent, qa);
    }

    [Fact]
    public async Task CanRejectQa_is_false_for_a_regular_non_QA_task()
    {
        var task = PulseTask.Create("Just a task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GetTaskQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.Data!.CanRejectQa.Should().BeFalse();
    }

    [Fact]
    public async Task CanRejectQa_is_true_for_the_QA_tasks_own_assignee()
    {
        var actorId = Guid.NewGuid();
        var (_, qa) = InQaPair(qaAssigneeId: actorId);

        var result = await CreateHandler().Handle(new GetTaskQuery(qa.Id, actorId, Roles.Engineer), default);

        result.Data!.CanRejectQa.Should().BeTrue();
    }

    [Fact]
    public async Task CanRejectQa_is_false_for_an_unrelated_engineer()
    {
        var (_, qa) = InQaPair(qaAssigneeId: Guid.NewGuid());

        var result = await CreateHandler().Handle(new GetTaskQuery(qa.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.Data!.CanRejectQa.Should().BeFalse();
    }

    [Fact]
    public async Task CanRejectQa_is_true_for_PMO_even_on_an_unassigned_QA_task()
    {
        var (_, qa) = InQaPair(qaAssigneeId: null);

        var result = await CreateHandler().Handle(new GetTaskQuery(qa.Id, Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.Data!.CanRejectQa.Should().BeTrue();
    }

    [Fact]
    public async Task CanRejectQa_is_true_for_the_reviewers_department_head()
    {
        var team = Team.Create("Eng team", department: "Engineering");
        var reviewer = Engineer.Create("Reviewer", "rev@test.io", "hash", Roles.Engineer, 20, 14);
        reviewer.AssignToTeam(team.Id);
        var deptHead = Engineer.Create("Dept Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        deptHead.AssignToTeam(team.Id);

        _engineers.Setup(e => e.GetByIdAsync(reviewer.Id, default)).ReturnsAsync(reviewer);
        _engineers.Setup(e => e.GetByIdAsync(deptHead.Id, default)).ReturnsAsync(deptHead);
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var (_, qa) = InQaPair(qaAssigneeId: reviewer.Id);

        var result = await CreateHandler().Handle(new GetTaskQuery(qa.Id, deptHead.Id, Roles.HeadOfRnD), default);

        result.Data!.CanRejectQa.Should().BeTrue();
    }

    [Fact]
    public async Task AssignedAt_reflects_when_the_current_assignee_was_assigned()
    {
        var task = PulseTask.Create("Assignable task", 3, Guid.NewGuid());
        var actor = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var before = DateTime.UtcNow;
        task.Assign(Guid.NewGuid(), actor);
        var after = DateTime.UtcNow;

        var result = await CreateHandler().Handle(new GetTaskQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.Data!.AssignedAt.Should().NotBeNull();
        result.Data!.AssignedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task AssignedAt_reflects_the_latest_reassignment_not_the_first_assignment()
    {
        var task = PulseTask.Create("Reassigned task", 3, Guid.NewGuid());
        var actor = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        task.Assign(Guid.NewGuid(), actor);
        var secondAssignee = Guid.NewGuid();
        task.Assign(secondAssignee, actor);

        var result = await CreateHandler().Handle(new GetTaskQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.Data!.AssigneeId.Should().Be(secondAssignee);
        result.Data!.AssignedAt.Should().Be(task.History.Last(h => h.Field == "assignee_id").ChangedAt);
    }

    [Fact]
    public async Task AssignedAt_is_null_for_a_never_assigned_task()
    {
        var task = PulseTask.Create("Unassigned task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GetTaskQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.Data!.AssignedAt.Should().BeNull();
    }
}
