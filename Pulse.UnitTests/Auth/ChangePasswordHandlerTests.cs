using Pulse.Application.Auth.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Auth;

public class ChangePasswordHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IBreachedPasswordChecker> _hibp = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private ChangePasswordHandler CreateHandler() =>
        new(_engineers.Object, _hasher.Object, _hibp.Object, _audit.Object);

    private static Engineer ActiveEngineer() =>
        Engineer.Create("Dev", "dev@pulse.io", "hashed_old", Roles.Engineer, 20, 14);

    [Fact]
    public async Task Returns_NOT_FOUND_when_user_does_not_exist()
    {
        _engineers.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Engineer?)null);

        var result = await CreateHandler().Handle(
            new ChangePasswordCommand(Guid.NewGuid(), "old", "newpassword12345!", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_WRONG_PASSWORD_when_current_password_incorrect()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("wrong", "hashed_old")).Returns(false);

        var result = await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "wrong", "NewPassword@12345!", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("WRONG_PASSWORD");
    }

    [Fact]
    public async Task Returns_PASSWORD_TOO_SHORT_for_new_password_under_12_chars()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("old", "hashed_old")).Returns(true);

        var result = await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "old", "short!", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("PASSWORD_TOO_SHORT");
    }

    [Fact]
    public async Task Returns_BREACHED_PASSWORD_when_hibp_flags_password()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("old", "hashed_old")).Returns(true);
        _hibp.Setup(h => h.IsBreachedAsync("NewPassword@12345!", default)).ReturnsAsync(true);

        var result = await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "old", "NewPassword@12345!", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BREACHED_PASSWORD");
    }

    [Fact]
    public async Task Changes_password_hash_on_success()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("old", "hashed_old")).Returns(true);
        _hasher.Setup(h => h.Hash("NewPassword@12345!")).Returns("hashed_new");
        _hibp.Setup(h => h.IsBreachedAsync(It.IsAny<string>(), default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "old", "NewPassword@12345!", null), default);

        result.IsSuccess.Should().BeTrue();
        engineer.PasswordHash.Should().Be("hashed_new");
    }

    [Fact]
    public async Task Logs_audit_entry_on_success()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("old", "hashed_old")).Returns(true);
        _hibp.Setup(h => h.IsBreachedAsync(It.IsAny<string>(), default)).ReturnsAsync(false);

        await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "old", "NewPassword@12345!", "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("AUTH_PASSWORD_CHANGED", engineer.Id, "1.2.3.4",
            It.IsAny<string?>(), default), Times.Once);
    }

    [Fact]
    public async Task Saves_engineer_on_success()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("old", "hashed_old")).Returns(true);
        _hibp.Setup(h => h.IsBreachedAsync(It.IsAny<string>(), default)).ReturnsAsync(false);

        await CreateHandler().Handle(
            new ChangePasswordCommand(engineer.Id, "old", "NewPassword@12345!", null), default);

        _engineers.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }
}
