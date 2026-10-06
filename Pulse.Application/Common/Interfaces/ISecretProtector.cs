namespace Pulse.Application.Common.Interfaces;

/// <summary>Encrypts secrets before they're stored in the database (e.g. organizations' Slack bot tokens).</summary>
public interface ISecretProtector
{
    /// <summary>False when no encryption key is configured — callers refuse to store secrets then.</summary>
    bool IsConfigured { get; }
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
