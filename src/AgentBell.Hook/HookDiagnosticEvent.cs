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

    /// <summary>Gets the session hash for a PermissionRequest event.</summary>
    [JsonPropertyName("sessionIdHash")]
    public string? SessionIdHash { get; init; }

    /// <summary>Gets a truncated SHA-256 reference for the turn identifier.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets whether a working directory was present, without storing the path.</summary>
    [JsonPropertyName("hasWorkingDirectory")]
    public bool HasWorkingDirectory { get; init; }

    /// <summary>Gets whether an assistant message was present, without storing its contents.</summary>
    [JsonPropertyName("hasAssistantMessage")]
    public bool HasAssistantMessage { get; init; }

    /// <summary>Gets the allow-listed tool category.</summary>
    [JsonPropertyName("toolCategory")]
    public string? ToolCategory { get; init; }

    /// <summary>Gets a fingerprint of the already-sanitized event identifier.</summary>
    [JsonPropertyName("eventIdHash")]
    public string? EventIdHash { get; init; }

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
            ThreadIdHash = IsSessionScopedLifecycle(metadata?.EventType)
                ? null
                : IdentifierHash.Create(metadata?.ThreadId),
            SessionIdHash = IsSessionScopedLifecycle(metadata?.EventType)
                ? IdentifierHash.Create(metadata?.ThreadId)
                : null,
            TurnIdHash = IdentifierHash.Create(metadata?.TurnId),
            HasWorkingDirectory = !string.IsNullOrWhiteSpace(metadata?.WorkingDirectory),
            HasAssistantMessage = !string.IsNullOrWhiteSpace(metadata?.LastAssistantMessage),
            ToolCategory = metadata?.ToolCategory,
            EventIdHash = string.IsNullOrWhiteSpace(metadata?.EventId)
                ? null
                : IdentifierHash.CreateFingerprint(metadata.EventId),
            Result = result.Code,
            HttpStatusCode = result.HttpStatusCode,
            ElapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds),
        };

    private static bool IsSessionScopedLifecycle(string? eventType) => eventType is
        SanitizedActionRequiredEvent.PermissionRequestEventType
        or SanitizedPostToolUseEvent.PostToolUseEventType;
}
