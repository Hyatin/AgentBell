using System.Security.Cryptography;
using System.Text;

namespace AgentBell.Contracts;

/// <summary>Creates deterministic irreversible references without a process-random salt.</summary>
public static class IdentifierHash
{
    /// <summary>Creates the established 12-character truncated SHA-256 identifier.</summary>
    public static string? Create(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>Creates a 24-character SHA-256 fingerprint for a non-secret composite value.</summary>
    public static string CreateFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }
}
