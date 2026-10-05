using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class ListTasksHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ISprintRepository> _sprints = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public ListTasksHandlerTests()
    {
        _subtasks
            .Setup(s => s.CountsByTaskIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, (int Done, int Total)>());
        _projects
            .Setup(p => p.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _projects
            .Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());
    }

    private ListTasksHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _projects.Object, _teams.Object, _sprints.Object, _subtasks.Object, _access.Object);

    private static PulseTask ActiveTask(Guid projectId, Guid? assigneeId = null)
    {
        var task = PulseTask.Create("Test task", 3, projectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        if (assigneeId.HasValue)
            task.Assign(assigneeId.Value, Guid.NewGuid());
        return task;
    }

    [Fact]
    public async Task Engineer_view_denormalizes_project_name_and_assignee_name_onto_each_row()
    {
        var assignee = Engineer.Create("Dana Dev", "dana@pulse.io", "hash", Roles.Engineer, 20, 14);
        var actorId = assignee.Id;
        var projectId = Guid.NewGuid();
        var task = ActiveTask(projectId, actorId);

        _tasks
            .Setup(r => r.ListAsync(null, actorId, null, null, null, null, 20, null,
                false, null, null, null, null, null, false, null, false, null, null, default))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects
            .Setup(r => r.GetNamesByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(projectId)), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [projectId] = "Project Alpha" });
        _engineers
            .Setup(r => r.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(actorId)), default))
            .ReturnsAsync(new[] { assignee });

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.Engineer, null, null, null, null, null, null, 20, null), default);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data!.Items.Single();
        dto.ProjectName.Should().Be("Project Alpha");
        dto.AssigneeName.Should().Be("Dana Dev");
    }

    [Fact]
    public async Task Manager_view_denormalizes_project_name_and_assignee_name_onto_each_row()
    {
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assignee = Engineer.Create("Priya PM's Pick", "priya@pulse.io", "hash", Roles.Engineer, 20, 14);
        var assigneeId = assignee.Id;
        var task = ActiveTask(projectId, assigneeId);

        _tasks
            .Setup(r => r.ListAsync(null, null, null, null, null, null, 20, null,
                false, null, null, null, null, null, false, null, false, null, null, default))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects
            .Setup(r => r.GetNamesByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(projectId)), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [projectId] = "Project Beta" });
        _engineers
            .Setup(r => r.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(assigneeId)), default))
            .ReturnsAsync(new[] { assignee });

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.ProjectManager, null, null, null, null, null, null, 20, null), default);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data!.Items.Single();
        dto.ProjectName.Should().Be("Project Beta");
        dto.AssigneeName.Should().Be("Priya PM's Pick");
    }

    [Fact]
    public async Task Unassigned_task_gets_null_assignee_name_without_error()
    {
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var task = ActiveTask(projectId);

        _tasks
            .Setup(r => r.ListAsync(null, null, null, null, null, null, 20, null,
                false, null, null, null, null, null, false, null, false, null, null, default))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects
            .Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [projectId] = "Project Gamma" });

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.ProjectManager, null, null, null, null, null, null, 20, null), default);

        var dto = result.Data!.Items.Single();
        dto.AssigneeName.Should().BeNull();
        _engineers.Verify(r => r.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default), Times.Never);
    }

    [Fact]
    public async Task Task_with_different_assignee_and_creator_denormalizes_both_names()
    {
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assignee = Engineer.Create("Dana Dev", "dana2@pulse.io", "hash", Roles.Engineer, 20, 14);
        var creator = Engineer.Create("Pat PM", "pat@pulse.io", "hash", Roles.ProjectManager, 20, 14);
        var task = PulseTask.Create("Test task", 3, projectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)), createdById: creator.Id);
        task.Assign(assignee.Id, creator.Id);

        _tasks
            .Setup(r => r.ListAsync(null, null, null, null, null, null, 20, null,
                false, null, null, null, null, null, false, null, false, null, null, default))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects
            .Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [projectId] = "Project Delta" });
        _engineers
            .Setup(r => r.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(assignee.Id) && ids.Contains(creator.Id)), default))
            .ReturnsAsync(new[] { assignee, creator });

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.ProjectManager, null, null, null, null, null, null, 20, null), default);

        var dto = result.Data!.Items.Single();
        dto.AssigneeName.Should().Be("Dana Dev");
        dto.CreatorName.Should().Be("Pat PM");
    }

    // ── NoAssignee (unclaimed-work browsing) ─────────────────────────────────

    [Fact]
    public async Task NoAssignee_without_a_projectId_returns_validation_error()
    {
        var actorId = Guid.NewGuid();

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.Engineer, null, null, null, null, null, null, 20, null, NoAssignee: true), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task NoAssignee_returns_forbidden_when_the_actor_has_no_connection_to_the_project()
    {
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        _access.Setup(a => a.CanAccessProjectAsync(projectId, actorId, Roles.Engineer, default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new ListTasksQuery(actorId, Roles.Engineer, projectId, null, null, null, null, null, 20, null, NoAssignee: true), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    // ── Personal tasks: only their owner may list them ────────────────────────

    private void SeedPersonalTaskList(Guid ownerId, out Guid personalProjectId, out PulseTask personalTask)
    {
        personalProjectId = Guid.NewGuid();
        personalTask = ActiveTask(personalProjectId, ownerId);
        var projectId = personalProjectId;
        var task = personalTask;
        _tasks
            .Setup(r => r.ListAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<Domain.Tasks.TaskStatus?>(),
                It.IsAny<TaskType?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<Guid>?>(), It.IsAny<IReadOnlyList<Guid>?>(),
                It.IsAny<IReadOnlyList<Guid>?>(), It.IsAny<Discipline?>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<Guid>?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects
            .Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { projectId });
        _projects
            .Setup(p => p.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _engineers
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Engineer>());
    }

    [Fact]
    public async Task Head_of_pmo_filtering_by_a_persons_assignee_does_not_get_their_personal_tasks()
    {
        var ownerId = Guid.NewGuid();
        SeedPersonalTaskList(ownerId, out _, out _);

        var result = await CreateHandler().Handle(
            new ListTasksQuery(Guid.NewGuid(), Roles.HeadOfPmo, null, ownerId, null, null, null, null, 20, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Owner_still_gets_their_own_personal_tasks_when_filtering_by_themselves()
    {
        var ownerId = Guid.NewGuid();
        var own = Project.CreatePersonal(ownerId, "Owner", "POW");
        _projects.Setup(p => p.GetPersonalProjectAsync(ownerId, It.IsAny<CancellationToken>())).ReturnsAsync(own);
        _projects.Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { own.Id });
        var task = ActiveTask(own.Id, ownerId);
        _tasks
            .Setup(r => r.ListAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<Domain.Tasks.TaskStatus?>(),
                It.IsAny<TaskType?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<Guid>?>(), It.IsAny<IReadOnlyList<Guid>?>(),
                It.IsAny<IReadOnlyList<Guid>?>(), It.IsAny<Discipline?>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<Guid>?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { task }, (string?)null));
        _projects.Setup(p => p.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _engineers.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Engineer>());

        var result = await CreateHandler().Handle(
            new ListTasksQuery(ownerId, Roles.HR, null, ownerId, null, null, null, null, 20, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().ContainSingle();
    }
}
