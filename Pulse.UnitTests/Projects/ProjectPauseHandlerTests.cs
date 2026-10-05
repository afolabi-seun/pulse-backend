using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Commands;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Projects;

public class ProjectPauseHandlerTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEpicRepository> _epics = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public ProjectPauseHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private static PulseTask ActiveTask(Guid projectId, Guid? assigneeId = null, DateOnly? dueDate = null)
    {
        var task = PulseTask.Create("Task", 3, projectId, dueDate: dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(assigneeId ?? Guid.NewGuid(), Guid.NewGuid());
        return task;
    }

    // ── PauseProjectHandler ───────────────────────────────────────────────────

    private PauseProjectHandler CreatePauseHandler() =>
        new(_projects.Object, _tasks.Object, _epics.Object, _audit.Object, _access.Object);

    [Fact]
    public async Task PauseProject_returns_NOT_FOUND_when_project_missing()
    {
        _projects.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Project?)null);

        var result = await CreatePauseHandler().Handle(new PauseProjectCommand(Guid.NewGuid(), Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task PauseProject_returns_FORBIDDEN_when_actor_cannot_access_project()
    {
        var project = Project.Create("Test");
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access
            .Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreatePauseHandler().Handle(new PauseProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task PauseProject_returns_BUSINESS_RULE_VIOLATION_when_already_paused()
    {
        var project = Project.Create("Test");
        project.Pause();
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _tasks.Setup(r => r.GetByProjectAsync(project.Id, default)).ReturnsAsync(Array.Empty<PulseTask>());

        var result = await CreatePauseHandler().Handle(new PauseProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task PauseProject_pauses_active_and_blocked_tasks_but_not_done_or_inQa()
    {
        var project = Project.Create("Test");
        var active = ActiveTask(project.Id);
        var blocked = ActiveTask(project.Id);
        blocked.FlagBlocker("waiting", Guid.NewGuid());
        var done = ActiveTask(project.Id);
        done.MarkDone(Guid.NewGuid());

        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _tasks.Setup(r => r.GetByProjectAsync(project.Id, default)).ReturnsAsync(new[] { active, blocked, done });

        var result = await CreatePauseHandler().Handle(new PauseProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        project.Status.Should().Be(ProjectStatus.Paused);
        active.Status.Should().Be(DomainTaskStatus.Paused);
        active.PausedByProject.Should().BeTrue();
        blocked.Status.Should().Be(DomainTaskStatus.Paused);
        blocked.PausedByProject.Should().BeTrue();
        done.Status.Should().Be(DomainTaskStatus.Done, "a done task should not be reopened by a project pause");
    }

    // ── ResumeProjectHandler ──────────────────────────────────────────────────

    private ResumeProjectHandler CreateResumeHandler() =>
        new(_projects.Object, _tasks.Object, _epics.Object, _escalationEvents.Object, _audit.Object, _access.Object);

    [Fact]
    public async Task ResumeProject_returns_BUSINESS_RULE_VIOLATION_when_not_paused()
    {
        var project = Project.Create("Test");
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);

        var result = await CreateResumeHandler().Handle(new ResumeProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task ResumeProject_resumes_only_tasks_it_paused_not_voluntarily_paused_ones()
    {
        var project = Project.Create("Test");
        project.Pause();

        var byProject = ActiveTask(project.Id);
        byProject.Pause(null, Guid.NewGuid(), byProject: true);

        var voluntary = ActiveTask(project.Id);
        voluntary.Pause("taking a break from this one", Guid.NewGuid());

        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _tasks.Setup(r => r.GetByProjectAsync(project.Id, default)).ReturnsAsync(new[] { byProject, voluntary });

        var result = await CreateResumeHandler().Handle(new ResumeProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        project.Status.Should().Be(ProjectStatus.Active);
        byProject.Status.Should().Be(DomainTaskStatus.Active, "the project resumed it");
        voluntary.Status.Should().Be(DomainTaskStatus.Paused, "the assignee paused it voluntarily, unrelated to the project hold");
    }

    [Fact]
    public async Task ResumeProject_shifts_due_dates_forward_for_resumed_tasks()
    {
        var project = Project.Create("Test");
        project.Pause();
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        var task = ActiveTask(project.Id, dueDate: dueDate);
        task.Pause(null, Guid.NewGuid(), byProject: true);
        typeof(PulseTask).GetProperty("PausedAt")!.SetValue(task, DateTime.UtcNow.AddDays(-3));

        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _tasks.Setup(r => r.GetByProjectAsync(project.Id, default)).ReturnsAsync(new[] { task });

        var result = await CreateResumeHandler().Handle(new ResumeProjectCommand(project.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(dueDate.AddDays(3));
    }
}
