using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class BulkCreateTasksHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public BulkCreateTasksHandlerTests()
    {
        _tasks.Setup(t => t.GetNextTaskNumberAsync(It.IsAny<Guid>(), default)).ReturnsAsync(1);
    }

    private BulkCreateTasksHandler CreateHandler() =>
        new(_tasks.Object, _projects.Object, _engineers.Object, _audit.Object, _access.Object);

    [Fact]
    public async Task Item_with_acceptance_criteria_sets_it_on_the_created_task()
    {
        var project = Project.Create("Alpha");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);

        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var item = new BulkTaskItem("Task with AC", null, 3, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), null,
            TaskType.Feature, "Given X, when Y, then Z.");
        var cmd = new BulkCreateTasksCommand(project.Id, [item], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        added!.AcceptanceCriteria.Should().Be("Given X, when Y, then Z.");
    }

    [Fact]
    public async Task Item_with_no_points_is_created_ungroomed()
    {
        var project = Project.Create("Alpha");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);

        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var item = new BulkTaskItem("Ungroomed task", null, null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), null);
        var cmd = new BulkCreateTasksCommand(project.Id, [item], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        result.Data!.Failed.Should().BeEmpty();
        added!.Points.Should().Be(0);
    }

    [Fact]
    public async Task Item_with_out_of_range_points_still_fails()
    {
        var project = Project.Create("Alpha");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);

        var item = new BulkTaskItem("Bad points task", null, 0, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), null);
        var cmd = new BulkCreateTasksCommand(project.Id, [item], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data!.Failed.Should().ContainSingle(f => f.Index == 0);
    }

    [Fact]
    public async Task Item_with_no_due_date_and_no_points_or_assignee_is_created_ungroomed()
    {
        var project = Project.Create("Alpha");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);

        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var item = new BulkTaskItem("Bare backlog item", null, null, null, null);
        var cmd = new BulkCreateTasksCommand(project.Id, [item], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        added!.DueDate.Should().BeNull();
    }

    [Fact]
    public async Task Item_with_points_but_no_due_date_is_rejected()
    {
        var project = Project.Create("Alpha");
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);

        var item = new BulkTaskItem("Pointed but unscheduled", null, 5, null, null);
        var cmd = new BulkCreateTasksCommand(project.Id, [item], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data!.Failed.Should().ContainSingle(f => f.Index == 0 && f.Error.Contains("due date"));
    }
}
