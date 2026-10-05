namespace Pulse.Application.Common.Interfaces;

/// <summary>All application settings resolved from environment variables at startup.</summary>
public interface IAppSettings
{
    // ── Database ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Connection used for schema migrations and Hangfire storage — must be a privileged
    /// (owner) role. Falls back to the runtime connection when DB_MIGRATION_CONNECTION is unset.
    /// </summary>
    string MigrationConnectionString { get; }

    // ── App ──────────────────────────────────────────────────────────────────
    string AppBaseUrl { get; }
    string[] AllowedOrigins { get; }
    int ActivationTokenExpiryDays { get; }
    int PasswordResetTokenExpiryMinutes { get; }

    // ── JWT ──────────────────────────────────────────────────────────────────
    string JwtSecretKey { get; }
    string JwtIssuer { get; }
    string JwtAudience { get; }
    int AccessTokenExpiryMinutes { get; }
    /// <summary>How long a refresh token may sit unused before a refresh attempt is rejected, even
    /// if still within its absolute 14-day expiry. Bounds how long an unattended, still-logged-in
    /// browser stays silently authenticated.</summary>
    int RefreshTokenIdleTimeoutMinutes { get; }

    // ── Mail ─────────────────────────────────────────────────────────────────
    string MailHost { get; }
    int MailPort { get; }
    string MailUser { get; }
    string MailPassword { get; }
    string MailFromName { get; }
    string MailFromAddress { get; }
    bool MailEnableSsl { get; }

    // ── AI alert explanations (optional) ────────────────────────────────────────
    /// <summary>Null when unset — IAlertExplainer degrades to no explanation (callers fall back to
    /// their own plain templated message) rather than failing startup. Unlike JwtSecretKey/MailHost,
    /// this integration is genuinely optional: Alert Rules works completely without it.</summary>
    string? AnthropicApiKey { get; }
    string AnthropicModel { get; }

    // ── Slack follow-up Q&A (optional) ──────────────────────────────────────────
    /// <summary>Both null unless a Slack App (not just an incoming webhook) has been installed —
    /// required only for follow-up Q&A in an alert's Slack thread, never for one-way delivery.</summary>
    string? SlackSigningSecret { get; }
    string? SlackBotToken { get; }

    // ── Google Chat follow-up Q&A (optional) ────────────────────────────────────
    /// <summary>The raw JSON key content (not a file path) for a Google Cloud service account with
    /// the Chat API's "chat.bot" scope — used to obtain OAuth access tokens for posting messages.
    /// Null unless a real Google Chat app has been configured; every downstream call degrades to a
    /// no-op without it, same shape as Slack's missing-credentials path.</summary>
    string? GoogleChatServiceAccountJson { get; }
    /// <summary>The expected `aud` claim on inbound Google Chat request tokens (the Cloud project
    /// number or configured audience URL from the Chat API's app configuration) — required to
    /// verify a request genuinely came from Google Chat rather than being merely well-formed.</summary>
    string? GoogleChatAudience { get; }
}
