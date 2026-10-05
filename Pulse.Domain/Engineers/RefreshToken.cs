using Pulse.Domain.Common;

namespace Pulse.Domain.Engineers;

public class RefreshToken : Entity
{
    public Guid EngineerID { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public bool IsRevoked { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    /// <summary>Stamped at creation and on every successful rotation — the clock an idle timeout
    /// measures from. Distinct from ExpiresAt: that's a fixed absolute cap from the original login
    /// (no sliding window); this is a rolling "time since last use" that resets each refresh.</summary>
    public DateTime LastUsedAt { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Create(Guid engineerId, string tokenHash, DateTime expiresAt) =>
        new() { EngineerID = engineerId, TokenHash = tokenHash, ExpiresAt = expiresAt, LastUsedAt = DateTime.UtcNow };

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsActive => !IsRevoked && !IsExpired;

    /// <summary>True once more than <paramref name="idleWindow"/> has passed since this token was
    /// last used to refresh a session, even if it's still within its absolute ExpiresAt window.</summary>
    public bool IsIdleExpired(TimeSpan idleWindow) => DateTime.UtcNow - LastUsedAt > idleWindow;

    public RefreshToken Rotate(string newTokenHash)
    {
        IsRevoked = true;
        ReplacedByTokenHash = newTokenHash;
        return Create(EngineerID, newTokenHash, ExpiresAt); // preserve original expiry — no sliding window
    }

    public void RevokeAll() => IsRevoked = true;
}
