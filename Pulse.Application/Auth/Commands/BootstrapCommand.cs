using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record BootstrapCommand(
    string Name,
    string Email,
    string Password,
    string? IpAddress) : IRequest<ServiceResult<AuthDto>>;

/// <summary>
/// Creates the very first Head of R&amp;D account. Fails with 409 Conflict if any engineer already exists.
/// This endpoint is unauthenticated and must only succeed once — it is the onboarding entry point.
/// </summary>
public class BootstrapHandler : IRequestHandler<BootstrapCommand, ServiceResult<AuthDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IAuditLogRepository _audit;

    public BootstrapHandler(
        IEngineerRepository engineers,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher hasher,
        IJwtService jwt,
        IAuditLogRepository audit)
    {
        _engineers = engineers;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _jwt = jwt;
        _audit = audit;
    }

    public async Task<ServiceResult<AuthDto>> Handle(BootstrapCommand cmd, CancellationToken ct)
    {
        if (await _engineers.AnyAsync(ct))
            return ServiceResult<AuthDto>.Fail("CONFLICT", "Bootstrap is only available on an empty instance.");

        var passwordHash = _hasher.Hash(cmd.Password);
        var engineer = Engineer.Create(cmd.Name, cmd.Email, passwordHash, Roles.HeadOfRnD,
            baselinePoints: 40, baselineCycleDays: 14);

        await _engineers.AddAsync(engineer, ct);

        var rawRefreshToken = _jwt.GenerateRefreshToken();
        var tokenHash = _jwt.HashToken(rawRefreshToken);
        var refreshToken = RefreshToken.Create(engineer.Id, tokenHash, DateTime.UtcNow.AddDays(14));

        await _refreshTokens.AddAsync(refreshToken, ct);
        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("BOOTSTRAP", engineer.Id, cmd.IpAddress,
            $"First account created: {engineer.Email}", ct);

        var accessToken = _jwt.GenerateAccessToken(engineer);

        return ServiceResult<AuthDto>.Ok(new AuthDto(
            accessToken,
            rawRefreshToken,
            new AuthUserDto(engineer.Id, engineer.Name, engineer.Email, engineer.Role,
                PermissionService.For(engineer.Role), CapabilityRegistry.ResolveFor(engineer.Role))));
    }
}
