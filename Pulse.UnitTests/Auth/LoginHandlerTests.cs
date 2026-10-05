using Pulse.Application.Auth.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Moq;
using FluentAssertions;

namespace Pulse.UnitTests.Auth;

public class LoginHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IRefreshTokenRepository> _refreshTokens = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IJwtService> _jwt = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    private LoginHandler CreateHandler() =>
        new(_engineers.Object, _refreshTokens.Object, _hasher.Object, _jwt.Object, _audit.Object, _emailQueue.Object, _settings.Object);

    private static Engineer ActiveEngineer(string email = "dev@pulse.io") =>
        Engineer.Create("Dev User", email, "hashed_pw", Roles.Engineer, 20, 14);

    [Fact]
    public async Task Returns_tokens_on_valid_credentials()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("correct", "hashed_pw")).Returns(true);
        _jwt.Setup(j => j.GenerateAccessToken(engineer)).Returns("access");
        _jwt.Setup(j => j.GenerateRefreshToken()).Returns("raw_refresh");
        _jwt.Setup(j => j.HashToken("raw_refresh")).Returns("hashed_refresh");

        var result = await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "correct", null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AccessToken.Should().Be("access");
        result.Data.RefreshToken.Should().Be("raw_refresh");
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_when_email_not_found()
    {
        _engineers.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), default)).ReturnsAsync((Engineer?)null);
        // Hasher must still be called (timing attack mitigation)
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await CreateHandler().Handle(new LoginCommand("nope@x.com", "pw", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_on_wrong_password()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("wrong", "hashed_pw")).Returns(false);

        var result = await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "wrong", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Increments_failed_attempts_on_wrong_password()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "wrong", null), default);

        engineer.FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Resets_failed_attempts_on_successful_login()
    {
        var engineer = ActiveEngineer();
        engineer.RecordFailedLogin(); // 1 previous failure
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("correct", "hashed_pw")).Returns(true);
        _jwt.Setup(j => j.GenerateRefreshToken()).Returns("r");
        _jwt.Setup(j => j.HashToken("r")).Returns("rh");

        await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "correct", null), default);

        engineer.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Returns_ACCOUNT_LOCKED_when_engineer_is_locked_out()
    {
        var engineer = ActiveEngineer();
        for (var i = 0; i < 5; i++) engineer.RecordFailedLogin(); // triggers lock
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);

        var result = await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "any", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Does_not_reveal_email_existence_via_error_code()
    {
        _engineers.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), default)).ReturnsAsync((Engineer?)null);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await CreateHandler().Handle(new LoginCommand("ghost@x.com", "pw", null), default);

        result.ErrorCode.Should().Be("UNAUTHORIZED");
        result.ErrorMessage.Should().Be("Invalid credentials.");
    }

    [Fact]
    public async Task Logs_audit_entry_on_successful_login()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify("correct", "hashed_pw")).Returns(true);
        _jwt.Setup(j => j.GenerateRefreshToken()).Returns("r");
        _jwt.Setup(j => j.HashToken("r")).Returns("rh");

        await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "correct", "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("AUTH_LOGIN", engineer.Id, "1.2.3.4", null, default), Times.Once);
    }

    [Fact]
    public async Task Logs_audit_entry_on_failed_login()
    {
        var engineer = ActiveEngineer();
        _engineers.Setup(r => r.GetByEmailAsync("dev@pulse.io", default)).ReturnsAsync(engineer);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        await CreateHandler().Handle(new LoginCommand("dev@pulse.io", "wrong", "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("AUTH_FAILED", engineer.Id, "1.2.3.4", null, default), Times.Once);
    }
}
