using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class ImportTasksHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IEpicRepository> _epics = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public ImportTasksHandlerTests()
    {
        // Default: caller is authorized. The access-check itself is covered by
        // ProjectAccessPolicyTests and the dedicated forbidden-path test below.
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private ImportTasksHandler CreateHandler() =>
        new(_tasks.Object, _projects.Object, _engineers.Object, _epics.Object, _audit.Object, _access.Object);

    private PulseTask? _added;

    private void SetupProject(Project project, IReadOnlyList<Engineer>? engineers = null, IReadOnlyList<PulseTask>? existingTasks = null)
    {
        _projects.Setup(p => p.ListActiveAsync(default)).ReturnsAsync([project]);
        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _epics.Setup(e => e.ListByProjectAsync(project.Id, default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(engineers ?? []);
        _tasks.Setup(t => t.GetByProjectAsync(project.Id, default)).ReturnsAsync(existingTasks ?? []);
        _tasks.Setup(t => t.GetNextTaskNumberAsync(project.Id, default)).ReturnsAsync(1);
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => _added = t)
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Row_missing_points_and_due_date_lands_in_Backlog_instead_of_being_rejected()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Untriaged task", null, null, 0, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Failures.Should().BeEmpty();
        result.Data.Created.Should().Be(1);
        _added!.Status.Should().Be(DomainTaskStatus.Backlog);
        _added.Points.Should().Be(0);
    }

    [Fact]
    public async Task Row_with_assignee_but_no_due_date_is_rejected()
    {
        // A due date becomes mandatory once a row carries enough shape to be scheduled — same
        // rule as single-task create and the JSON bulk importer.
        var project = Project.Create("Alpha");
        var engineer = Engineer.Create("Bob", "bob@test.io", "hash", Roles.Engineer, 10, 14);
        SetupProject(project, [engineer]);

        var row = new ImportTaskRow(project.Name, "Untriaged but assigned", null, null, 0, null, TaskType.Feature, engineer.Email, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("due date"));
    }

    [Fact]
    public async Task Row_with_points_but_no_due_date_is_rejected()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Pointed but unscheduled", null, null, 5, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("due date"));
    }

    [Fact]
    public async Task Row_with_points_due_date_and_assignee_activates_normally()
    {
        var project = Project.Create("Alpha");
        var engineer = Engineer.Create("Bob", "bob@test.io", "hash", Roles.Engineer, 10, 14);
        SetupProject(project, [engineer]);

        var row = new ImportTaskRow(project.Name, "Groomed task", null, null, 5,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), TaskType.Feature, engineer.Email, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        _added!.Status.Should().Be(DomainTaskStatus.Active);
        _added.Points.Should().Be(5);
    }

    [Fact]
    public async Task Row_with_points_above_13_is_rejected()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Way too big", null, null, 500,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("points"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }

    [Fact]
    public async Task Row_matching_an_already_existing_task_title_is_rejected_as_a_duplicate()
    {
        // The exact scenario a re-uploaded CSV produces: the task already exists from a prior
        // import, so this row should be skipped rather than creating a second copy.
        var project = Project.Create("Alpha");
        var existing = PulseTask.Create("Story point", 1, project.Id);
        SetupProject(project, existingTasks: [existing]);

        var row = new ImportTaskRow(project.Name, "Story point", null, null, 0, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("already exists") && f.Error.Contains("duplicate"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }

    [Fact]
    public async Task Two_identical_rows_in_the_same_file_create_only_the_first()
    {
        // The same duplicate check must also catch a CSV that repeats a row internally, not just
        // a title that collides with something already in the database.
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Repeated row", null, null, 0, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row, row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        result.Data.Failures.Should().ContainSingle(f => f.Row == 3 && f.Error.Contains("duplicate"));
    }

    [Fact]
    public async Task Row_with_acceptance_criteria_sets_it_on_the_created_task()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Task with AC", null, "Given X, when Y, then Z.", 3,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        _added!.AcceptanceCriteria.Should().Be("Given X, when Y, then Z.");
    }

    [Fact]
    public async Task Row_with_a_valid_priority_sets_it_on_the_created_task()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Prioritized task", null, null, 3,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), TaskType.Feature, null, null, Priority: 2);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(1);
        _added!.Priority.Should().Be(2);
    }

    [Fact]
    public async Task Row_with_an_out_of_range_priority_is_rejected()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Bad priority", null, null, 3,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), TaskType.Feature, null, null, Priority: 9);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("priority"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }

    [Fact]
    public async Task Row_with_only_a_priority_still_requires_a_due_date()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Prioritized but unscheduled", null, null, 0, null, TaskType.Feature, null, null, Priority: 3);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("due date"));
    }

    [Fact]
    public async Task Row_targeting_a_project_the_actor_cannot_access_is_rejected()
    {
        var project = Project.Create("Alpha");
        SetupProject(project);
        _access
            .Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var row = new ImportTaskRow(project.Name, "No access", null, null, 0, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null, Roles.HeadOfRnD);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("access"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }

    [Fact]
    public async Task Row_targeting_a_paused_project_is_rejected()
    {
        var project = Project.Create("Alpha");
        project.Pause();
        SetupProject(project);

        var row = new ImportTaskRow(project.Name, "Paused project row", null, null, 0, null, TaskType.Feature, null, null);
        var cmd = new ImportTasksCommand([row], Guid.NewGuid(), null);

        var result = await CreateHandler().Handle(cmd, default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("paused"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }
}
