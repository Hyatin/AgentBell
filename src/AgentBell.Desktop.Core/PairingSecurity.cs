using System.Security.Cryptography;
using System.Text;

namespace AgentBell.Desktop;

/// <summary>Protects pairing-token bytes before they are persisted.</summary>
public interface IPairingTokenProtector
{
    /// <summary>Protects plaintext bytes for the current Windows user.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Recovers plaintext bytes for the current Windows user.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Uses Windows DPAPI CurrentUser scope for pairing-token protection.</summary>
public sealed class WindowsDpapiPairingTokenProtector : IPairingTokenProtector
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("AgentBell.PairingToken.v1");

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(
            plaintext,
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return ProtectedData.Unprotect(
            protectedData,
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
    }
}

/// <summary>Owns a 256-bit random pairing credential and compares it in fixed time.</summary>
public sealed class PairingToken : IDisposable
{
    /// <summary>The exact number of random token bytes.</summary>
    public const int ByteLength = 32;

    private readonly byte[] _bytes;
    private bool _disposed;

    /// <summary>Initializes a token from exactly 32 random bytes.</summary>
    public PairingToken(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException("A pairing token must contain 32 bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
        Value = Base64Url.Encode(_bytes);
    }

    /// <summary>Gets the Base64URL form used only for explicit pairing and authentication.</summary>
    public string Value { get; }

    /// <summary>Generates a token with 256 bits of cryptographic randomness.</summary>
    public static PairingToken Generate()
    {
        var bytes = new byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            return new PairingToken(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Compares a presented Base64URL token without ordinary string early exit.</summary>
    public bool Matches(string? presentedToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> candidate = stackalloc byte[ByteLength];
        var decoded = Base64Url.TryDecodeExact(presentedToken, candidate);
        var equal = CryptographicOperations.FixedTimeEquals(_bytes, candidate);
        CryptographicOperations.ZeroMemory(candidate);
        return decoded & equal;
    }

    /// <summary>Returns a temporary copy for DPAPI protection.</summary>
    internal byte[] CopyBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bytes.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _disposed = true;
    }
}

/// <summary>Provides strict unpadded Base64URL encoding for security identifiers.</summary>
public static class Base64Url
{
    /// <summary>Encodes bytes with the URL-safe alphabet and no padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Decodes a value only when it exactly fills the supplied destination.</summary>
    public static bool TryDecodeExact(string? value, Span<byte> destination)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 43
            || value.Any(character =>
                !(character is >= 'A' and <= 'Z')
                && !(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character is not '-' and not '_'))
        {
            return false;
        }

        var padding = (4 - (value.Length % 4)) % 4;
        var normalized = value.Replace('-', '+').Replace('_', '/') + new string('=', padding);
        try
        {
            var bytes = Convert.FromBase64String(normalized);
            try
            {
                if (bytes.Length != destination.Length)
                {
                    return false;
                }

                bytes.CopyTo(destination);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
