using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class GetQaQueueQueryTests
{
    private readonly Mock<ITaskRepository> _tasks = new();

    private GetQaQueueHandler CreateHandler() => new(_tasks.Object);

    [Fact]
    public async Task Returns_only_qa_subtasks_assigned_to_the_caller()
    {
        var actorId = Guid.NewGuid();

        var qaTask = PulseTask.Create("[QA] Fix the widget", 2, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)));
        qaTask.SetParentTaskId(Guid.NewGuid());
        qaTask.Assign(actorId, Guid.NewGuid());

        var ownRegularTask = PulseTask.Create("Unrelated task assigned to me", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)));
        ownRegularTask.Assign(actorId, Guid.NewGuid());

        _tasks.Setup(r => r.GetActiveByAssigneeAsync(actorId, default))
            .ReturnsAsync(new[] { qaTask, ownRegularTask });

        var result = await CreateHandler().Handle(new GetQaQueueQuery(actorId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle().Which.Id.Should().Be(qaTask.Id);
    }

    [Fact]
    public async Task Returns_empty_when_caller_has_no_open_qa_tasks()
    {
        var actorId = Guid.NewGuid();
        _tasks.Setup(r => r.GetActiveByAssigneeAsync(actorId, default))
            .ReturnsAsync(Array.Empty<PulseTask>());

        var result = await CreateHandler().Handle(new GetQaQueueQuery(actorId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }
}
