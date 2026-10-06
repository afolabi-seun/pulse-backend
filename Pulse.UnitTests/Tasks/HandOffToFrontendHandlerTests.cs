using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class HandOffToFrontendHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();

    public HandOffToFrontendHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private HandOffToFrontendHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _projects.Object, _audit.Object, _access.Object,
            _settings.Object, _checkIns.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private static PulseTask FlaggedTask(Guid backendDev)
    {
        var task = PulseTask.Create("Two-stage task", 5, Guid.NewGuid());
        task.Assign(backendDev, backendDev);
        task.SetRequiresFrontendHandoff(true);
        return task;
    }

    private static Engineer FrontendEngineer(string email = "frontend@test.io")
    {
        var engineer = Engineer.Create("Frontend Dev", email, "hash", Roles.Engineer, 20, 14);
        engineer.SetDiscipline(Discipline.Frontend);
        return engineer;
    }

    [Fact]
    public async Task Returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_is_neither_the_assignee_nor_has_project_access()
    {
        var backendDev = Guid.NewGuid();
        var task = FlaggedTask(backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, Guid.NewGuid(), Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Rejects_a_target_engineer_whose_discipline_is_not_Frontend()
    {
        var backendDev = Guid.NewGuid();
        var task = FlaggedTask(backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var backendEngineer = Engineer.Create("Backend Dev", "backend@test.io", "hash", Roles.Engineer, 20, 14);
        backendEngineer.SetDiscipline(Discipline.Backend);
        _engineers.Setup(e => e.GetByIdAsync(backendEngineer.Id, default)).ReturnsAsync(backendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, backendEngineer.Id, backendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.AssigneeId.Should().Be(backendDev);
    }

    [Fact]
    public async Task Rejects_a_task_that_is_not_flagged_for_a_frontend_handoff()
    {
        var backendDev = Guid.NewGuid();
        var task = PulseTask.Create("Regular task", 5, Guid.NewGuid());
        task.Assign(backendDev, backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var frontendEngineer = FrontendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(frontendEngineer.Id, default)).ReturnsAsync(frontendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, frontendEngineer.Id, backendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Handing_off_records_a_check_in_entry_for_the_backend_engineer()
    {
        var backendDev = Guid.NewGuid();
        var task = FlaggedTask(backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var frontendEngineer = FrontendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(frontendEngineer.Id, default)).ReturnsAsync(frontendEngineer);
        CheckIn? added = null;
        _checkIns.Setup(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()))
            .Callback<CheckIn, CancellationToken>((ci, _) => added = ci).Returns(Task.CompletedTask);

        // PMO performs the hand-off on the backend engineer's behalf — the entry is still theirs.
        var pmo = Guid.NewGuid();
        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, frontendEngineer.Id, pmo, null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.EngineerId.Should().Be(backendDev);
        added.ProjectId.Should().Be(task.ProjectId);
        added.Completed.Should().Be("Handed off to Frontend: Two-stage task");
    }

    [Fact]
    public async Task A_rejected_handoff_records_no_check_in_entry()
    {
        var backendDev = Guid.NewGuid();
        var task = FlaggedTask(backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var notFrontend = Engineer.Create("Backend Dev", "be2@test.io", "hash", Roles.Engineer, 20, 14);
        notFrontend.SetDiscipline(Discipline.Backend);
        _engineers.Setup(e => e.GetByIdAsync(notFrontend.Id, default)).ReturnsAsync(notFrontend);

        await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, notFrontend.Id, backendDev, null, Roles.Engineer), default);

        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Succeeds_and_reassigns_to_the_frontend_engineer()
    {
        var backendDev = Guid.NewGuid();
        var task = FlaggedTask(backendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var frontendEngineer = FrontendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(frontendEngineer.Id, default)).ReturnsAsync(frontendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToFrontendCommand(task.Id, frontendEngineer.Id, backendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(frontendEngineer.Id);
        task.CurrentStage.Should().Be(TaskStage.Frontend);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, frontendEngineer.Id, default), Times.Once);
    }
}
