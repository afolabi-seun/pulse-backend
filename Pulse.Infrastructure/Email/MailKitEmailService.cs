using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Pulse.Infrastructure.Email;

public class MailKitEmailService : IEmailService, IDirectEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly bool _enableSsl;
    private readonly ILogger<MailKitEmailService> _logger;
    private readonly IFailedEmailRepository _failedEmails;

    private const int MaxAttempts = 3;

    public MailKitEmailService(IAppSettings settings, ILogger<MailKitEmailService> logger, IFailedEmailRepository failedEmails)
    {
        _host         = settings.MailHost;
        _port         = settings.MailPort;
        _username     = settings.MailUser;
        _password     = settings.MailPassword;
        _fromAddress  = settings.MailFromAddress;
        _fromName     = settings.MailFromName;
        _enableSsl    = settings.MailEnableSsl;
        _logger       = logger;
        _failedEmails = failedEmails;
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        Exception? lastEx = null;
        var attempts = 0;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            attempts = attempt;
            try
            {
                await TrySendAsync(to, subject, htmlBody, ct);
                return;
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError(ex, "Email auth failed sending to {To} (subject: {Subject}). Check SMTP credentials.", to, subject);
                lastEx = ex;
                break;
            }
            catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
            {
                _logger.LogError(ex, "Permanent SMTP rejection sending to {To} (subject: {Subject}, status: {Status}).", to, subject, ex.StatusCode);
                lastEx = ex;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastEx = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Email send attempt {Attempt}/{Max} failed for {To} (subject: {Subject}). Retrying in {Delay}s.",
                    attempt, MaxAttempts, to, subject, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogError(ex, "Email send failed after {Max} attempts for {To} (subject: {Subject}).", MaxAttempts, to, subject);
                break;
            }
        }

        await PersistFailureAsync(to, subject, htmlBody, attempts, lastEx);
        throw lastEx!;
    }

    // IDirectEmailSender — retry loop without persistence side-effect; used by retry command handlers.
    async Task IDirectEmailSender.SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await TrySendAsync(to, subject, htmlBody, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastEx = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Direct email attempt {Attempt}/{Max} failed for {To}. Retrying in {Delay}s.", attempt, MaxAttempts, to, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogError(ex, "Direct email failed after {Max} attempts for {To}.", MaxAttempts, to);
                break;
            }
        }

        throw lastEx!;
    }

    private async Task PersistFailureAsync(string to, string subject, string htmlBody, int attempts, Exception? ex)
    {
        try
        {
            var record = FailedEmail.Create(to, subject, htmlBody, ex?.Message, attempts);
            await _failedEmails.AddAsync(record);
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Could not persist failed email record for {To} (subject: {Subject}).", to, subject);
        }
    }

    private async Task TrySendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_fromName, _fromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        var socketOptions = _enableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_host, _port, socketOptions, ct);
        await client.AuthenticateAsync(_username, _password, ct);
        await client.SendAsync(message, ct);

        // The message is already accepted by the SMTP server at this point — a failure tearing
        // down the connection (e.g. the server closing abruptly after QUIT) is not a delivery
        // failure and must not be allowed to unwind into the caller's retry/failure-tracking logic,
        // which would otherwise record an email that was actually sent as still failed.
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SMTP disconnect failed after successful send to {To} (subject: {Subject}) — ignoring.", to, subject);
        }
    }
}
