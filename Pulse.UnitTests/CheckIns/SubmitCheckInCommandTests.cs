using Pulse.Application.CheckIns.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.CheckIns;

public class SubmitCheckInCommandTests
{
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private SubmitCheckInHandler CreateHandler() => new(_checkIns.Object, _audit.Object);

    [Fact]
    public async Task Creates_a_new_check_in_when_none_exists_for_that_engineer_and_date()
    {
        var engineerId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        _checkIns.Setup(r => r.GetByEngineerAndDateAsync(engineerId, date, null, default)).ReturnsAsync((CheckIn?)null);

        var cmd = new SubmitCheckInCommand(engineerId, date, "Did stuff", "Do more", null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Completed.Should().Be("Did stuff");
        _checkIns.Verify(r => r.AddAsync(It.IsAny<CheckIn>(), default), Times.Once);
        _audit.Verify(a => a.LogAsync("CHECK_IN_SUBMITTED", engineerId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Updates_the_existing_check_in_in_place_instead_of_creating_a_duplicate()
    {
        var engineerId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = CheckIn.Submit(engineerId, date, "Old text", "Old plan", null);
        _checkIns.Setup(r => r.GetByEngineerAndDateAsync(engineerId, date, null, default)).ReturnsAsync(existing);

        var cmd = new SubmitCheckInCommand(engineerId, date, "New text", "New plan", "Blocked on review", engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Id.Should().Be(existing.Id);
        result.Data!.Completed.Should().Be("New text");
        result.Data!.PlannedNext.Should().Be("New plan");
        result.Data!.Blockers.Should().Be("Blocked on review");
        _checkIns.Verify(r => r.AddAsync(It.IsAny<CheckIn>(), default), Times.Never);
        _checkIns.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _audit.Verify(a => a.LogAsync("CHECK_IN_UPDATED", engineerId, null, It.IsAny<string>(), default), Times.Once);
    }
}
