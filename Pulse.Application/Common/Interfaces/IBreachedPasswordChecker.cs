namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// Checks a candidate password against the Have I Been Pwned breach corpus using k-anonymity.
/// The full password is never transmitted — only the first 5 characters of its SHA-1 hash.
/// </summary>
public interface IBreachedPasswordChecker
{
    /// <summary>Returns true if the password appears in the HIBP breach database.</summary>
    Task<bool> IsBreachedAsync(string password, CancellationToken ct = default);
}
