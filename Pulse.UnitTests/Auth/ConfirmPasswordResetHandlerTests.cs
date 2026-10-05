using Pulse.Application.Auth.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Auth;

public class ConfirmPasswordResetHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IRefreshTokenRepository> _refreshTokens = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IJwtService> _jwt = new();
    private readonly Mock<IBreachedPasswordChecker> _hibp = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private ConfirmPasswordResetHandler CreateHandler() =>
        new(_engineers.Object, _refreshTokens.Object, _hasher.Object, _jwt.Object, _hibp.Object, _audit.Object);

    private static Engineer EngineerWithResetToken()
    {
        var e = Engineer.Create("Dev", "dev@pulse.io", "hash", Roles.Engineer, 20, 14);
        e.SetPasswordResetToken("token_hash", DateTime.UtcNow.AddHours(1));
        return e;
    }

    [Fact]
    public async Task Returns_Ok_and_updates_password_on_valid_token_and_clean_password()
    {
        var engineer = EngineerWithResetToken();

        _jwt.Setup(j => j.HashToken("raw")).Returns("token_hash");
        _engineers.Setup(r => r.GetByResetTokenHashAsync("token_hash", default)).ReturnsAsync(engineer);
        _hibp.Setup(h => h.IsBreachedAsync("Str0ng!Pass12", default)).ReturnsAsync(false);
        _hasher.Setup(h => h.Hash("Str0ng!Pass12")).Returns("new_hash");

        var result = await CreateHandler().Handle(
            new ConfirmPasswordResetCommand("raw", "Str0ng!Pass12", null), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_INVALID_TOKEN_when_token_not_found()
    {
        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("token_hash");
        _engineers.Setup(r => r.GetByResetTokenHashAsync("token_hash", default)).ReturnsAsync((Engineer?)null);

        var result = await CreateHandler().Handle(
            new ConfirmPasswordResetCommand("bad_token", "Str0ng!Pass12", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task Returns_BREACHED_PASSWORD_when_hibp_flags_password()
    {
        var engineer = EngineerWithResetToken();

        _jwt.Setup(j => j.HashToken("raw")).Returns("token_hash");
        _engineers.Setup(r => r.GetByResetTokenHashAsync("token_hash", default)).ReturnsAsync(engineer);
        _hibp.Setup(h => h.IsBreachedAsync("password123", default)).ReturnsAsync(true);

        var result = await CreateHandler().Handle(
            new ConfirmPasswordResetCommand("raw", "password123", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BREACHED_PASSWORD");
    }

    [Fact]
    public async Task Clears_reset_token_from_engineer_on_success()
    {
        var engineer = EngineerWithResetToken();

        _jwt.Setup(j => j.HashToken("raw")).Returns("token_hash");
        _engineers.Setup(r => r.GetByResetTokenHashAsync("token_hash", default)).ReturnsAsync(engineer);
        _hibp.Setup(h => h.IsBreachedAsync(It.IsAny<string>(), default)).ReturnsAsync(false);
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("new_hash");

        await CreateHandler().Handle(
            new ConfirmPasswordResetCommand("raw", "Str0ng!Pass12", null), default);

        engineer.PasswordResetToken.Should().BeNull();
        engineer.PasswordResetTokenExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Revokes_all_refresh_tokens_on_success()
    {
        var engineer = EngineerWithResetToken();

        _jwt.Setup(j => j.HashToken("raw")).Returns("token_hash");
        _engineers.Setup(r => r.GetByResetTokenHashAsync("token_hash", default)).ReturnsAsync(engineer);
        _hibp.Setup(h => h.IsBreachedAsync(It.IsAny<string>(), default)).ReturnsAsync(false);
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("new_hash");

        await CreateHandler().Handle(
            new ConfirmPasswordResetCommand("raw", "Str0ng!Pass12", null), default);

        _refreshTokens.Verify(r => r.RevokeAllForEngineerAsync(engineer.Id, default), Times.Once);
    }
}
