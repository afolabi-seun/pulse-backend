using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class TaskNumberAllocatorTests
{
    private readonly Mock<ITaskRepository> _tasks = new();

    [Fact]
    public async Task First_call_for_a_project_queries_the_repository()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetNextTaskNumberAsync(projectId, default)).ReturnsAsync(5);
        var allocator = new TaskNumberAllocator(_tasks.Object);

        var number = await allocator.NextAsync(projectId, default);

        number.Should().Be(5);
    }

    [Fact]
    public async Task Repeated_calls_for_the_same_project_increment_locally_without_requerying()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetNextTaskNumberAsync(projectId, default)).ReturnsAsync(5);
        var allocator = new TaskNumberAllocator(_tasks.Object);

        var first  = await allocator.NextAsync(projectId, default);
        var second = await allocator.NextAsync(projectId, default);
        var third  = await allocator.NextAsync(projectId, default);

        first.Should().Be(5);
        second.Should().Be(6);
        third.Should().Be(7);
        _tasks.Verify(t => t.GetNextTaskNumberAsync(projectId, default), Times.Once);
    }

    [Fact]
    public async Task Different_projects_are_tracked_independently()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        _tasks.Setup(t => t.GetNextTaskNumberAsync(projectA, default)).ReturnsAsync(1);
        _tasks.Setup(t => t.GetNextTaskNumberAsync(projectB, default)).ReturnsAsync(9);
        var allocator = new TaskNumberAllocator(_tasks.Object);

        (await allocator.NextAsync(projectA, default)).Should().Be(1);
        (await allocator.NextAsync(projectB, default)).Should().Be(9);
        (await allocator.NextAsync(projectA, default)).Should().Be(2);
        (await allocator.NextAsync(projectB, default)).Should().Be(10);
    }
}
