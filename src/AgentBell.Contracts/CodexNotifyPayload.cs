using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>
/// Represents the untrusted JSON payload supplied by Codex to a notify process.
/// Every documented field is optional and unknown fields are ignored.
/// </summary>
public sealed record CodexNotifyPayload
{
    /// <summary>Gets the Codex event type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Gets the optional Codex thread identifier.</summary>
    [JsonPropertyName("thread-id")]
    public string? ThreadId { get; init; }

    /// <summary>Gets the optional Codex turn identifier.</summary>
    [JsonPropertyName("turn-id")]
    public string? TurnId { get; init; }

    /// <summary>Gets the optional working directory. It must never be logged in full.</summary>
    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets input messages for compatibility parsing only. Their contents must never be logged or forwarded to a phone.</summary>
    [JsonPropertyName("input-messages")]
    public IReadOnlyList<string>? InputMessages { get; init; }

    /// <summary>Gets the optional last assistant message. Its full contents must never be logged.</summary>
    [JsonPropertyName("last-assistant-message")]
    public string? LastAssistantMessage { get; init; }
}

