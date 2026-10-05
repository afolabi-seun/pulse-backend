using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class GetTaskMentionCandidatesQueryTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IEngineerRepository> _engineers = new();

    private GetTaskMentionCandidatesHandler CreateHandler() => new(_tasks.Object, _access.Object, _engineers.Object);

    private readonly Guid _actorId = Guid.NewGuid();

    [Fact]
    public async Task Returns_NOT_FOUND_for_a_missing_task()
    {
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new GetTaskMentionCandidatesQuery(taskId, _actorId, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_caller_cannot_access_the_task()
    {
        var task = PulseTask.Create("T", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, _actorId, Roles.Engineer, default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new GetTaskMentionCandidatesQuery(task.Id, _actorId, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Org_wide_read_only_viewers_bypass_the_access_check(string role)
    {
        var task = PulseTask.Create("T", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(Array.Empty<Guid>());
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(Array.Empty<Engineer>());

        var result = await CreateHandler().Handle(
            new GetTaskMentionCandidatesQuery(task.Id, _actorId, role), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanViewTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Includes_the_tasks_creator_even_when_they_have_no_other_project_access()
    {
        var creator = Engineer.Create("PMO Filer", "pmo@test.io", "hash", Roles.ProductManager, 20, 14);
        // ProductManager IS a global role in real life, but the point here is the access-set
        // mock returns nothing for them regardless — the creator must still be added explicitly.
        var task = PulseTask.Create("T", 3, Guid.NewGuid(), createdById: creator.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, _actorId, Roles.Engineer, default)).ReturnsAsync(true);
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(Array.Empty<Guid>());
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(creator.Id)), default))
            .ReturnsAsync(new[] { creator });

        var result = await CreateHandler().Handle(
            new GetTaskMentionCandidatesQuery(task.Id, _actorId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Should().ContainSingle(e => e.Id == creator.Id);
    }

    [Fact]
    public async Task Returns_the_accessible_engineers_sorted_by_name()
    {
        var bob = Engineer.Create("Bob", "bob@test.io", "hash", Roles.Engineer, 20, 14);
        var alice = Engineer.Create("Alice", "alice@test.io", "hash", Roles.Engineer, 20, 14);
        var task = PulseTask.Create("T", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, _actorId, Roles.Engineer, default)).ReturnsAsync(true);
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(new[] { bob.Id, alice.Id });
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(bob.Id) && ids.Contains(alice.Id)), default))
            .ReturnsAsync(new[] { bob, alice });

        var result = await CreateHandler().Handle(
            new GetTaskMentionCandidatesQuery(task.Id, _actorId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Select(e => e.Name).Should().Equal("Alice", "Bob");
    }
}
