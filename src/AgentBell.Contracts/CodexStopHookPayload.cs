using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>
/// Represents the untrusted JSON object supplied on standard input for a Codex Stop command Hook.
/// Every documented data field is optional; unknown fields, including transcript_path, are ignored.
/// </summary>
public sealed record CodexStopHookPayload
{
    /// <summary>Gets the optional Codex session identifier.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>Gets the optional active Codex turn identifier.</summary>
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    /// <summary>Gets the optional working directory. It must never be logged in full.</summary>
    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the Hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    /// <summary>Gets the optional last assistant message. Its contents must never be logged.</summary>
    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    /// <summary>Gets whether this turn was already continued by a Stop Hook.</summary>
    [JsonPropertyName("stop_hook_active")]
    public bool? StopHookActive { get; init; }

    /// <summary>Gets the optional Codex permission mode.</summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    /// <summary>Gets the optional active model slug.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

