using System.Security.Cryptography;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;

namespace Pulse.Application.Users;

/// <summary>
/// The invite-by-activation-link flow shared by every way an account is created for someone else
/// (CreateUserCommand, CreateOrganizationCommand): the account gets a random password nobody knows and a
/// password-reset token, and the person sets their own password from the emailed link.
/// </summary>
public static class ActivationInvite
{
    /// <summary>A random password hash for an account whose owner hasn't set one yet.</summary>
    public static string UnusablePasswordHash(IPasswordHasher hasher) =>
        hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Puts a fresh activation token on the engineer; returns the raw token for the link.</summary>
    public static string IssueToken(Engineer engineer, IJwtService jwt, IAppSettings settings)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        engineer.SetPasswordResetToken(jwt.HashToken(rawToken), DateTime.UtcNow.AddDays(settings.ActivationTokenExpiryDays));
        return rawToken;
    }

    public static void Send(IEmailQueue emailQueue, IAppSettings settings, string name, string email, string rawToken,
        string? organizationName = null)
    {
        var activationLink = $"{settings.AppBaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        var invitedTo = organizationName is null
            ? "<strong>Pulse</strong>"
            : $"<strong>{System.Net.WebUtility.HtmlEncode(organizationName)}</strong> on <strong>Pulse</strong>";
        var body = $"""
            <p>Hi {System.Net.WebUtility.HtmlEncode(name)},</p>
            <p>You've been invited to {invitedTo} — an engineering team management platform that helps track tasks, sprints, check-ins, and team capacity.</p>
            <p>Click the button below to set your password and access your account:</p>
            {EmailTemplate.Button(activationLink, "Accept invitation")}
            {EmailTemplate.Muted($"This link expires in {settings.ActivationTokenExpiryDays} day(s). If you were not expecting this invitation, you can safely ignore this email.")}
            """;
        emailQueue.Enqueue(email, "You've been invited to Pulse", EmailTemplate.Layout(body));
    }
}
