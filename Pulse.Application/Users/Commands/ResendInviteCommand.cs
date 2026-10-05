using System.Security.Cryptography;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Users.Commands;

public record ResendInviteCommand(Guid UserId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<bool>>;

/// <summary>
/// Generates a fresh activation token for an existing user and resends the welcome/activation email.
/// Useful when the original link expired or the email was not received.
/// </summary>
public class ResendInviteHandler : IRequestHandler<ResendInviteCommand, ServiceResult<bool>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IJwtService _jwt;
    private readonly IEmailQueue _emailQueue;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;

    public ResendInviteHandler(
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

    public async Task<ServiceResult<bool>> Handle(ResendInviteCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(cmd.UserId, ct);
        if (engineer is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", "User not found.");

        if (!engineer.IsActive)
            return ServiceResult<bool>.Fail("INVALID_OPERATION", "Cannot resend invite to a deactivated user.");

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = _jwt.HashToken(rawToken);
        engineer.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddDays(_settings.ActivationTokenExpiryDays));

        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("USER_INVITE_RESENT", cmd.ActorId, cmd.IpAddress,
            $"Resent invite to user {engineer.Id} ({engineer.Email})", ct);

        var activationLink = $"{_settings.AppBaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        var body = $"""
            <p>Hi {engineer.Name},</p>
            <p>Your invitation to <strong>Pulse</strong> has been refreshed. Use the link below to set your password and access your account:</p>
            {EmailTemplate.Button(activationLink, "Accept invitation")}
            {EmailTemplate.Muted($"This link expires in {_settings.ActivationTokenExpiryDays} day(s). If you were not expecting this, you can safely ignore this email.")}
            """;
        _emailQueue.Enqueue(engineer.Email, "Your Pulse invitation has been refreshed", EmailTemplate.Layout(body));

        return ServiceResult<bool>.Ok(true);
    }
}
