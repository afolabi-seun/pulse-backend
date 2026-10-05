using Pulse.Application.Common.Interfaces;

namespace Pulse.Api.Configuration;

public class AppSettings : IAppSettings
{
    // ── App ──────────────────────────────────────────────────────────────────
    public string AppBaseUrl { get; init; } = null!;
    public string[] AllowedOrigins { get; init; } = null!;
    public int ActivationTokenExpiryDays { get; init; }
    public int PasswordResetTokenExpiryMinutes { get; init; }

    // ── Database ─────────────────────────────────────────────────────────────
    public string DatabaseConnectionString { get; init; } = null!;
    public string MigrationConnectionString { get; init; } = null!;

    // ── JWT ──────────────────────────────────────────────────────────────────
    public string JwtSecretKey { get; init; } = null!;
    public string JwtIssuer { get; init; } = null!;
    public string JwtAudience { get; init; } = null!;
    public int AccessTokenExpiryMinutes { get; init; }
    public int RefreshTokenIdleTimeoutMinutes { get; init; }

    // ── Mail ─────────────────────────────────────────────────────────────────
    public string MailHost { get; init; } = null!;
    public int MailPort { get; init; }
    public string MailUser { get; init; } = null!;
    public string MailPassword { get; init; } = null!;
    public string MailFromName { get; init; } = null!;
    public string MailFromAddress { get; init; } = null!;
    public bool MailEnableSsl { get; init; }

    // ── AI alert explanations ────────────────────────────────────────────────
    public string? AnthropicApiKey { get; init; }
    public string AnthropicModel { get; init; } = null!;

    // ── Slack follow-up Q&A ──────────────────────────────────────────────────
    public string? SlackSigningSecret { get; init; }
    public string? SlackBotToken { get; init; }

    // ── Google Chat follow-up Q&A ────────────────────────────────────────────
    public string? GoogleChatServiceAccountJson { get; init; }
    public string? GoogleChatAudience { get; init; }

    public static AppSettings FromEnvironment() => new()
    {
        DatabaseConnectionString        = Env("DB_CONNECTION"),
        // Privileged connection for migrations + Hangfire; falls back to the runtime connection.
        MigrationConnectionString       = Environment.GetEnvironmentVariable("DB_MIGRATION_CONNECTION") ?? Env("DB_CONNECTION"),
        JwtSecretKey                    = Env("JWT_SECRET_KEY"),
        JwtIssuer                       = Env("JWT_ISSUER"),
        JwtAudience                     = Env("JWT_AUDIENCE"),
        AccessTokenExpiryMinutes        = ParseInt("JWT_ACCESS_EXPIRY_TIME"),
        // Optional — defaults to 60 so existing deployments without this env var keep working.
        RefreshTokenIdleTimeoutMinutes  = ParseIntOrDefault("JWT_REFRESH_IDLE_TIMEOUT_MINUTES", 60),
        ActivationTokenExpiryDays       = ParseInt("JWT_VERIFY_EMAIL_EXPIRY_TIME"),
        PasswordResetTokenExpiryMinutes = ParseInt("JWT_FORGET_PASSWORD_EXPIRY_TIME"),
        AppBaseUrl                      = Env("APP_BASE_URL"),
        AllowedOrigins                  = Env("ALLOWED_ORIGINS")
                                              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MailHost                        = Env("Mail_Host"),
        MailPort                        = ParseInt("Mail_Port"),
        MailUser                        = Env("Mail_User"),
        MailPassword                    = Env("Mail_Password"),
        MailFromName                    = Env("Mail_FromName"),
        MailFromAddress                 = Env("Mail_FromAddress"),
        MailEnableSsl                   = ParseBool("Mail_EnableSsl"),
        // All four below are genuinely optional — read directly (never via Env(), which throws)
        // so a deployment without an Anthropic key or a Slack App configured still starts cleanly.
        AnthropicApiKey                 = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        AnthropicModel                  = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-5",
        SlackSigningSecret              = Environment.GetEnvironmentVariable("SLACK_SIGNING_SECRET"),
        SlackBotToken                   = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN"),
        GoogleChatServiceAccountJson    = Environment.GetEnvironmentVariable("GOOGLE_CHAT_SERVICE_ACCOUNT_JSON"),
        GoogleChatAudience              = Environment.GetEnvironmentVariable("GOOGLE_CHAT_AUDIENCE"),
    };

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Required environment variable '{key}' is not set.");

    private static int ParseInt(string key)
    {
        var raw = Env(key);
        return int.TryParse(raw, out var v) ? v
            : throw new InvalidOperationException($"Environment variable '{key}' must be a valid integer, got '{raw}'.");
    }

    private static int ParseIntOrDefault(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(raw)) return fallback;
        return int.TryParse(raw, out var v) ? v
            : throw new InvalidOperationException($"Environment variable '{key}' must be a valid integer, got '{raw}'.");
    }

    private static bool ParseBool(string key)
    {
        var raw = Env(key);
        return bool.TryParse(raw, out var v) ? v
            : throw new InvalidOperationException($"Environment variable '{key}' must be 'true' or 'false', got '{raw}'.");
    }
}
