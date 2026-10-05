using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class HandOffToBackendHandlerTests
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

    public HandOffToBackendHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private HandOffToBackendHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _projects.Object, _audit.Object, _access.Object,
            _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object, _checkIns.Object);

    private static PulseTask TaskWithFrontendDev(Guid backendDev, Guid frontendDev)
    {
        var task = PulseTask.Create("Two-stage task", 5, Guid.NewGuid());
        task.Assign(backendDev, backendDev);
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);
        return task;
    }

    private static Engineer BackendEngineer(string email = "backend@test.io")
    {
        var engineer = Engineer.Create("Backend Dev", email, "hash", Roles.Engineer, 20, 14);
        engineer.SetDiscipline(Discipline.Backend);
        return engineer;
    }

    [Fact]
    public async Task Returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new HandOffToBackendCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Rejects_a_target_engineer_whose_discipline_is_not_Backend()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = TaskWithFrontendDev(backendDev, frontendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var frontendEngineer = Engineer.Create("Frontend Dev", "frontend2@test.io", "hash", Roles.Engineer, 20, 14);
        frontendEngineer.SetDiscipline(Discipline.Frontend);
        _engineers.Setup(e => e.GetByIdAsync(frontendEngineer.Id, default)).ReturnsAsync(frontendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToBackendCommand(task.Id, frontendEngineer.Id, frontendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.AssigneeId.Should().Be(frontendDev);
    }

    [Fact]
    public async Task Rejects_a_task_still_at_the_Backend_stage()
    {
        var backendDev = Guid.NewGuid();
        var task = PulseTask.Create("Not yet handed off", 5, Guid.NewGuid());
        task.Assign(backendDev, backendDev);
        task.SetRequiresFrontendHandoff(true);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var backendEngineer = BackendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(backendEngineer.Id, default)).ReturnsAsync(backendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToBackendCommand(task.Id, backendEngineer.Id, backendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Handing_back_records_a_check_in_entry_for_the_frontend_engineer()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = TaskWithFrontendDev(backendDev, frontendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var newBackendEngineer = BackendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(newBackendEngineer.Id, default)).ReturnsAsync(newBackendEngineer);

        // An existing check-in for the day is appended to, not replaced.
        var existing = CheckIn.Submit(frontendDev, DateOnly.FromDateTime(DateTime.UtcNow), "Fixed styling", string.Empty, null, task.ProjectId);
        _checkIns.Setup(c => c.GetByEngineerAndDateAsync(frontendDev, It.IsAny<DateOnly>(), task.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new HandOffToBackendCommand(task.Id, newBackendEngineer.Id, Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        existing.Completed.Should().Be($"Fixed styling\nHanded off to Backend: {task.Title}");
        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Succeeds_and_reassigns_back_to_the_backend_engineer()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = TaskWithFrontendDev(backendDev, frontendDev);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var newBackendEngineer = BackendEngineer();
        _engineers.Setup(e => e.GetByIdAsync(newBackendEngineer.Id, default)).ReturnsAsync(newBackendEngineer);

        var result = await CreateHandler().Handle(
            new HandOffToBackendCommand(task.Id, newBackendEngineer.Id, frontendDev, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(newBackendEngineer.Id);
        task.CurrentStage.Should().Be(TaskStage.Backend);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, newBackendEngineer.Id, default), Times.Once);
    }
}
