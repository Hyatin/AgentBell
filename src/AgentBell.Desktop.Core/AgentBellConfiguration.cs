using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace AgentBell.Desktop;

/// <summary>Represents the non-event local M2 configuration file.</summary>
public sealed record AgentBellConfiguration
{
    /// <summary>Gets the current protocol version.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    /// <summary>Gets the stable non-secret device identifier.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    /// <summary>Gets the display-only computer name.</summary>
    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; init; }

    /// <summary>Gets only the DPAPI-protected token ciphertext.</summary>
    [JsonPropertyName("encryptedPairingToken")]
    public string? EncryptedPairingToken { get; init; }

    /// <summary>Gets the most recently selected LAN port.</summary>
    [JsonPropertyName("lastLanPort")]
    public int? LastLanPort { get; init; }

    /// <summary>Gets when the configuration was first created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when the configuration was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Coordinates configuration defaults, token recovery, and safe updates.</summary>
public sealed class PairingConfigurationManager
{
    private readonly AgentBellConfigStore _store;
    private readonly IPairingTokenProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _deviceNameProvider;
    private readonly Func<int, bool> _lanPortValidator;

    /// <summary>Initializes the manager with replaceable security and clock collaborators.</summary>
    public PairingConfigurationManager(
        AgentBellConfigStore store,
        IPairingTokenProtector protector,
        TimeProvider? timeProvider = null,
        Func<string>? deviceNameProvider = null,
        Func<int, bool>? lanPortValidator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deviceNameProvider = deviceNameProvider ?? (() => Environment.MachineName);
        _lanPortValidator = lanPortValidator ?? LanPortRange.Contains;
    }

    /// <summary>Loads a usable configuration or creates and atomically saves a replacement.</summary>
    public async Task<PairingConfigurationLoadResult> LoadOrCreateAsync(
        CancellationToken cancellationToken)
    {
        var load = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!load.PersistenceSucceeded)
        {
            return PairingConfigurationLoadResult.Unavailable("config_unavailable");
        }

        var now = _timeProvider.GetUtcNow();
        var existing = load.Configuration;
        PairingToken? token = TryRecoverToken(existing?.EncryptedPairingToken);
        var tokenWasRegenerated = token is null;
        token ??= PairingToken.Generate();

        try
        {
            var createdAt = existing?.CreatedAt > DateTimeOffset.MinValue
                ? existing.CreatedAt
                : now;
            var deviceId = string.IsNullOrWhiteSpace(existing?.DeviceId)
                ? CreateDeviceId()
                : existing.DeviceId;
            var deviceName = NormalizeDeviceName(existing?.DeviceName)
                ?? NormalizeDeviceName(_deviceNameProvider())
                ?? "Windows PC";
            var lastLanPort = existing?.LastLanPort is int port && _lanPortValidator(port)
                ? (int?)port
                : null;
            var needsSave = existing is null
                || load.CorruptFileRecovered
                || tokenWasRegenerated
                || existing.ProtocolVersion != AgentBell.Contracts.AgentBellProtocol.ProtocolVersion
                || !string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal)
                || !string.Equals(existing.DeviceName, deviceName, StringComparison.Ordinal)
                || existing.LastLanPort != lastLanPort
                || existing.UpdatedAt <= DateTimeOffset.MinValue;

            var protectedToken = existing?.EncryptedPairingToken;
            if (tokenWasRegenerated || string.IsNullOrWhiteSpace(protectedToken))
            {
                protectedToken = ProtectToken(token);
                needsSave = true;
            }

            var configuration = new AgentBellConfiguration
            {
                ProtocolVersion = AgentBell.Contracts.AgentBellProtocol.ProtocolVersion,
                DeviceId = deviceId,
                DeviceName = deviceName,
                EncryptedPairingToken = protectedToken,
                LastLanPort = lastLanPort,
                CreatedAt = createdAt,
                UpdatedAt = needsSave ? now : existing!.UpdatedAt,
            };

            if (needsSave
                && !await _store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false))
            {
                token.Dispose();
                return PairingConfigurationLoadResult.Unavailable("config_write_failed");
            }

            return PairingConfigurationLoadResult.Available(
                new PairingConfigurationSession(
                    configuration,
                    token,
                    _store,
                    _timeProvider,
                    _lanPortValidator),
                tokenWasRegenerated,
                load.CorruptFileRecovered);
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or FormatException
            or ArgumentException)
        {
            token.Dispose();
            return PairingConfigurationLoadResult.Unavailable("config_security_failed");
        }
    }

    private PairingToken? TryRecoverToken(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
            plaintext = _protector.Unprotect(protectedBytes);
            return plaintext.Length == PairingToken.ByteLength
                ? new PairingToken(plaintext)
                : null;
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or FormatException
            or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private string ProtectToken(PairingToken token)
    {
        var plaintext = token.CopyBytes();
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = _protector.Protect(plaintext);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private static string CreateDeviceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var result = Base64Url.Encode(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    private static string? NormalizeDeviceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var enumerator = StringInfo.GetTextElementEnumerator(trimmed);
        var elements = new List<string>(64);
        while (elements.Count < 64 && enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return string.Concat(elements);
    }

}

/// <summary>Owns an authenticated configuration for the current Desktop process.</summary>
public sealed class PairingConfigurationSession : IDisposable
{
    private readonly AgentBellConfigStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, bool> _lanPortValidator;

    internal PairingConfigurationSession(
        AgentBellConfiguration configuration,
        PairingToken token,
        AgentBellConfigStore store,
        TimeProvider timeProvider,
        Func<int, bool> lanPortValidator)
    {
        Configuration = configuration;
        Token = token;
        _store = store;
        _timeProvider = timeProvider;
        _lanPortValidator = lanPortValidator;
    }

    /// <summary>Gets the current non-secret configuration.</summary>
    public AgentBellConfiguration Configuration { get; private set; }

    /// <summary>Gets the in-memory pairing credential.</summary>
    public PairingToken Token { get; }

    /// <summary>Persists the actual selected LAN port using atomic replacement.</summary>
    public async Task<bool> UpdateLanPortAsync(int port, CancellationToken cancellationToken)
    {
        if (!_lanPortValidator(port))
        {
            return false;
        }

        if (Configuration.LastLanPort == port)
        {
            return true;
        }

        var updated = Configuration with
        {
            LastLanPort = port,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        if (!await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Configuration = updated;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => Token.Dispose();
}

/// <summary>Reports whether secure M2 configuration is available.</summary>
public sealed record PairingConfigurationLoadResult(
    bool IsAvailable,
    PairingConfigurationSession? Session,
    string ResultCode,
    bool TokenRegenerated,
    bool CorruptFileRecovered)
{
    internal static PairingConfigurationLoadResult Available(
        PairingConfigurationSession session,
        bool tokenRegenerated,
        bool corruptFileRecovered) =>
        new(true, session, "success", tokenRegenerated, corruptFileRecovered);

    internal static PairingConfigurationLoadResult Unavailable(string resultCode) =>
        new(false, null, resultCode, false, false);
}

/// <summary>Defines the fixed M2 LAN port range.</summary>
public static class LanPortRange
{
    /// <summary>The first candidate LAN port.</summary>
    public const int FirstPort = 17864;

    /// <summary>The last candidate LAN port.</summary>
    public const int LastPort = 17874;

    /// <summary>Gets whether a port belongs to the immutable production LAN range.</summary>
    public static bool Contains(int port) => port is >= FirstPort and <= LastPort;
}
