using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class GroomOwnTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private GroomOwnTaskHandler CreateHandler() => new(_tasks.Object, _audit.Object);

    private static PulseTask SelfCreatedBacklogTask(Guid creatorId)
    {
        var task = PulseTask.Create("Self-created task", 0, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)), createdById: creatorId);
        task.Assign(creatorId, creatorId);
        return task;
    }

    [Fact]
    public async Task Creator_can_groom_their_own_backlog_task_and_it_activates()
    {
        var creatorId = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 3, 2, creatorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(3);
        task.Priority.Should().Be(2);
        task.Status.Should().Be(DomainTaskStatus.Active);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _audit.Verify(a => a.LogAsync("TASK_SELF_GROOMED", creatorId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Priority_is_optional()
    {
        var creatorId = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 5, null, creatorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(5);
        task.Priority.Should().BeNull();
    }

    [Fact]
    public async Task Someone_other_than_the_creator_is_forbidden()
    {
        var creatorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 3, null, otherId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.Points.Should().Be(0);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Reassigned_away_from_the_creator_is_forbidden_even_for_the_creator()
    {
        var creatorId = Guid.NewGuid();
        var newAssignee = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        task.Assign(newAssignee, creatorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 3, null, creatorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Already_groomed_task_is_rejected()
    {
        var creatorId = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        task.SetPoints(3, creatorId); // already groomed -> Active

        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 5, null, creatorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.Points.Should().Be(3);
    }

    [Fact]
    public async Task Zero_points_is_rejected_with_a_clear_message()
    {
        var creatorId = Guid.NewGuid();
        var task = SelfCreatedBacklogTask(creatorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(task.Id, 0, null, creatorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Missing_task_returns_not_found()
    {
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(new GroomOwnTaskCommand(taskId, 3, null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }
}
