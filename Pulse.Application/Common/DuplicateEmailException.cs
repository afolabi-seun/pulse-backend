namespace Pulse.Application.Common;

/// <summary>
/// Saving an engineer hit the app-wide unique email index. Engineer email is unique across every
/// organization (one account per email), but a caller can only look up engineers in their own org — the
/// org query filters and RLS hide the rest — so an in-app "does this email exist" check can't see a clash
/// with another org. The repository translates the database's unique-violation into this, so handlers
/// can answer 409 instead of failing with a 500.
/// </summary>
/// <remarks><see cref="Email"/> is known only when a single account was being saved; for a batch the
/// database doesn't say which row clashed (constraint details are hidden outside development).</remarks>
public class DuplicateEmailException(string? email)
    : Exception(email is null ? "An account with one of these emails already exists." : $"An account with email '{email}' already exists.")
{
    public string? Email { get; } = email;
}
