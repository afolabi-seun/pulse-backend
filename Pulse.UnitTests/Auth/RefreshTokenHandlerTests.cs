using Pulse.Application.Auth.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Auth;

public class RefreshTokenHandlerTests
{
    private readonly Mock<IRefreshTokenRepository> _refreshTokens = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IJwtService> _jwt = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RefreshTokenHandlerTests() =>
        _settings.Setup(s => s.RefreshTokenIdleTimeoutMinutes).Returns(60);

    private RefreshTokenHandler CreateHandler() =>
        new(_refreshTokens.Object, _engineers.Object, _jwt.Object, _audit.Object, _settings.Object);

    private static Engineer ActiveEngineer() =>
        Engineer.Create("Dev", "dev@pulse.io", "hash", Roles.Engineer, 20, 14);

    private static RefreshToken ActiveToken(Guid engineerId) =>
        RefreshToken.Create(engineerId, "hash", DateTime.UtcNow.AddDays(7));

    [Fact]
    public async Task Returns_new_tokens_on_valid_refresh_token()
    {
        var engineer = ActiveEngineer();
        var stored = ActiveToken(engineer.Id);

        _jwt.Setup(j => j.HashToken("raw")).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(stored);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _jwt.Setup(j => j.GenerateRefreshToken()).Returns("new_raw");
        _jwt.Setup(j => j.HashToken("new_raw")).Returns("new_hash");
        _jwt.Setup(j => j.GenerateAccessToken(engineer)).Returns("access");

        var result = await CreateHandler().Handle(new RefreshTokenCommand("raw", null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AccessToken.Should().Be("access");
        result.Data.RefreshToken.Should().Be("new_raw");
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_when_token_not_found()
    {
        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync((RefreshToken?)null);

        var result = await CreateHandler().Handle(new RefreshTokenCommand("unknown", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_when_token_is_expired()
    {
        var expired = RefreshToken.Create(Guid.NewGuid(), "hash", DateTime.UtcNow.AddDays(-1));

        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(expired);

        var result = await CreateHandler().Handle(new RefreshTokenCommand("raw", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Revokes_all_sessions_and_returns_UNAUTHORIZED_on_replay_attack()
    {
        var engineerId = Guid.NewGuid();
        var revoked = RefreshToken.Create(engineerId, "hash", DateTime.UtcNow.AddDays(7));
        revoked.RevokeAll(); // simulate already-revoked token

        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(revoked);

        var result = await CreateHandler().Handle(new RefreshTokenCommand("raw", "1.2.3.4"), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
        _refreshTokens.Verify(r => r.RevokeAllForEngineerAsync(engineerId, default), Times.Once);
        _audit.Verify(a => a.LogAsync("AUTH_REPLAY_ATTACK", engineerId, "1.2.3.4", null, default), Times.Once);
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_when_token_is_idle_expired_even_within_absolute_expiry()
    {
        var engineer = ActiveEngineer();
        // Still well within its 7-day absolute ExpiresAt, but hasn't been used in 61 minutes —
        // past the 60-minute idle window this test's settings mock configures.
        var stored = RefreshToken.Create(engineer.Id, "hash", DateTime.UtcNow.AddDays(7));
        typeof(RefreshToken).GetProperty(nameof(RefreshToken.LastUsedAt))!
            .SetValue(stored, DateTime.UtcNow.AddMinutes(-61));

        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(stored);

        var result = await CreateHandler().Handle(new RefreshTokenCommand("raw", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task Returns_UNAUTHORIZED_when_engineer_is_inactive()
    {
        var engineer = ActiveEngineer();
        engineer.Deactivate();
        var stored = ActiveToken(engineer.Id);

        _jwt.Setup(j => j.HashToken(It.IsAny<string>())).Returns("hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(stored);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);

        var result = await CreateHandler().Handle(new RefreshTokenCommand("raw", null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task Logs_audit_entry_on_successful_refresh()
    {
        var engineer = ActiveEngineer();
        var stored = ActiveToken(engineer.Id);

        _jwt.Setup(j => j.HashToken("raw")).Returns("hash");
        _jwt.Setup(j => j.HashToken("new_raw")).Returns("new_hash");
        _refreshTokens.Setup(r => r.GetByHashAsync("hash", default)).ReturnsAsync(stored);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _jwt.Setup(j => j.GenerateRefreshToken()).Returns("new_raw");

        await CreateHandler().Handle(new RefreshTokenCommand("raw", "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("AUTH_REFRESH", engineer.Id, "1.2.3.4", null, default), Times.Once);
    }
}
