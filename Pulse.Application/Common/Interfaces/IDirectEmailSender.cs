namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// Raw SMTP send with built-in retry. Does NOT persist FailedEmail records.
/// Use this in retry command handlers that manage their own failure records.
/// </summary>
public interface IDirectEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
