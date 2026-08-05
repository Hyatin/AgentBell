using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Contains only the allow-listed metadata permitted in M0 diagnostic logs.</summary>
public sealed record HookDiagnosticEvent
{
    /// <summary>Gets the diagnostic timestamp.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the event type, when available.</summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; init; }

    /// <summary>Gets a truncated SHA-256 reference for the thread identifier.</summary>
    [JsonPropertyName("threadIdHash")]
    public string? ThreadIdHash { get; init; }

    /// <summary>Gets a truncated SHA-256 reference for the turn identifier.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets whether a working directory was present, without storing the path.</summary>
    [JsonPropertyName("hasWorkingDirectory")]
    public bool HasWorkingDirectory { get; init; }

    /// <summary>Gets whether an assistant message was present, without storing its contents.</summary>
    [JsonPropertyName("hasAssistantMessage")]
    public bool HasAssistantMessage { get; init; }

    /// <summary>Gets the stable processing result.</summary>
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    /// <summary>Gets the optional HTTP status code.</summary>
    [JsonPropertyName("httpStatus")]
    public int? HttpStatusCode { get; init; }

    /// <summary>Gets the total Hook processing duration in milliseconds.</summary>
    [JsonPropertyName("elapsedMs")]
    public required long ElapsedMilliseconds { get; init; }

    /// <summary>Creates an allow-listed diagnostic record from a payload and processing result.</summary>
    public static HookDiagnosticEvent Create(
        CodexNotifyPayload? payload,
        ForwardResult result,
        TimeSpan elapsed) =>
        Create(
            payload is null ? null : HookEventMetadata.FromNotify(payload),
            result,
            elapsed);

    /// <summary>Creates an allow-listed diagnostic record from normalized event metadata.</summary>
    public static HookDiagnosticEvent Create(
        HookEventMetadata? metadata,
        ForwardResult result,
        TimeSpan elapsed) =>
        new()
        {
            Timestamp = DateTimeOffset.Now,
            EventType = metadata?.EventType,
            ThreadIdHash = HashIdentifier(metadata?.ThreadId),
            TurnIdHash = HashIdentifier(metadata?.TurnId),
            HasWorkingDirectory = !string.IsNullOrWhiteSpace(metadata?.WorkingDirectory),
            HasAssistantMessage = !string.IsNullOrWhiteSpace(metadata?.LastAssistantMessage),
            Result = result.Code,
            HttpStatusCode = result.HttpStatusCode,
            ElapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds),
        };

    private static string? HashIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }
}
