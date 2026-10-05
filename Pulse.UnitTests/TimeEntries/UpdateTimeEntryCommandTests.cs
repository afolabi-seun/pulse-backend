using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Domain.Projects;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class UpdateTimeEntryCommandTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private UpdateTimeEntryHandler CreateHandler() => new(_timeEntries.Object, _tasks.Object, _projects.Object, _audit.Object);

    [Fact]
    public async Task Fails_with_not_found_when_the_entry_does_not_exist()
    {
        var id = Guid.NewGuid();
        _timeEntries.Setup(r => r.GetByIdAsync(id, default)).ReturnsAsync((TimeEntry?)null);

        var cmd = new UpdateTimeEntryCommand(id, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null, Guid.NewGuid(), null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_actor_does_not_own_the_entry()
    {
        var ownerId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();
        var entry = TimeEntry.Log(ownerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);
        _timeEntries.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);

        var cmd = new UpdateTimeEntryCommand(entry.Id, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 2, null, otherActorId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _timeEntries.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_not_found_when_the_project_does_not_exist()
    {
        var ownerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var entry = TimeEntry.Log(ownerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);
        _timeEntries.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);
        _projects.Setup(r => r.GetByIdAsync(projectId, default)).ReturnsAsync((Project?)null);

        var cmd = new UpdateTimeEntryCommand(entry.Id, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, projectId, 1, null, ownerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
        _timeEntries.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Updates_the_entry_when_owned_by_the_actor()
    {
        var ownerId = Guid.NewGuid();
        var entry = TimeEntry.Log(ownerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);
        _timeEntries.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);

        var cmd = new UpdateTimeEntryCommand(entry.Id, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 3, "updated", ownerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Hours.Should().Be(3);
        result.Data!.Note.Should().Be("updated");
        _timeEntries.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_UPDATED", ownerId, null, It.IsAny<string>(), default), Times.Once);
    }
}
