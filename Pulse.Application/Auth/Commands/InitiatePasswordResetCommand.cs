using System.Security.Cryptography;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record InitiatePasswordResetCommand(string Email, string? IpAddress) : IRequest<ServiceResult<bool>>;

/// <summary>
/// Generates a password-reset token and emails it to the engineer.
/// Always returns success regardless of whether the email is registered — prevents email enumeration.
/// The plain token is emailed; only its SHA-256 hash is stored on the engineer record.
/// </summary>
public class InitiatePasswordResetHandler : IRequestHandler<InitiatePasswordResetCommand, ServiceResult<bool>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IJwtService _jwt;
    private readonly IEmailQueue _emailQueue;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;

    public InitiatePasswordResetHandler(
        IEngineerRepository engineers,
        IJwtService jwt,
        IEmailQueue emailQueue,
        IAuditLogRepository audit,
        IAppSettings settings)
    {
        _engineers = engineers;
        _jwt = jwt;
        _emailQueue = emailQueue;
        _audit = audit;
        _settings = settings;
    }

    public async Task<ServiceResult<bool>> Handle(InitiatePasswordResetCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByEmailAsync(cmd.Email, ct);

        if (engineer is null || !engineer.IsActive)
            return ServiceResult<bool>.Ok(true); // silent — no email enumeration

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = _jwt.HashToken(rawToken);
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.PasswordResetTokenExpiryMinutes);

        engineer.SetPasswordResetToken(tokenHash, expiresAt);
        await _engineers.SaveChangesAsync(ct);

        var resetLink = $"{_settings.AppBaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        var body = $"""
            <p>Hi {engineer.Name},</p>
            <p>You requested a password reset for your Pulse account.</p>
            {EmailTemplate.Button(resetLink, "Reset password")}
            {EmailTemplate.Muted($"This link expires in {_settings.PasswordResetTokenExpiryMinutes} minutes. If you did not request this, you can safely ignore this email.")}
            """;

        _emailQueue.Enqueue(engineer.Email, "Reset your Pulse password", EmailTemplate.Layout(body));
        await _audit.LogAsync("AUTH_PASSWORD_RESET_INITIATED", engineer.Id, cmd.IpAddress, ct: ct);

        return ServiceResult<bool>.Ok(true);
    }
}
