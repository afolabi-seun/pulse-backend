using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class DeleteTimeEntryCommandTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private DeleteTimeEntryHandler CreateHandler() => new(_timeEntries.Object, _audit.Object);

    [Fact]
    public async Task Fails_with_not_found_when_the_entry_does_not_exist()
    {
        var id = Guid.NewGuid();
        _timeEntries.Setup(r => r.GetByIdAsync(id, default)).ReturnsAsync((TimeEntry?)null);

        var result = await CreateHandler().Handle(new DeleteTimeEntryCommand(id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_actor_does_not_own_the_entry()
    {
        var ownerId = Guid.NewGuid();
        var entry = TimeEntry.Log(ownerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);
        _timeEntries.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);

        var result = await CreateHandler().Handle(new DeleteTimeEntryCommand(entry.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _timeEntries.Verify(r => r.DeleteAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Deletes_the_entry_when_owned_by_the_actor()
    {
        var ownerId = Guid.NewGuid();
        var entry = TimeEntry.Log(ownerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);
        _timeEntries.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);

        var result = await CreateHandler().Handle(new DeleteTimeEntryCommand(entry.Id, ownerId, null), default);

        result.IsSuccess.Should().BeTrue();
        _timeEntries.Verify(r => r.DeleteAsync(entry, default), Times.Once);
        _timeEntries.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_DELETED", ownerId, null, It.IsAny<string>(), default), Times.Once);
    }
}
