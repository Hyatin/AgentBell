using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>
/// Contains only the fields needed in memory while handling an untrusted Codex
/// PermissionRequest Hook. Tool input and descriptions are deliberately excluded.
/// </summary>
public sealed record CodexPermissionRequestPayload
{
    /// <summary>Gets the optional session identifier used only for hashing.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>Gets the optional turn identifier used only for hashing.</summary>
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    /// <summary>Gets the optional tool-use identifier used only for hashing.</summary>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    /// <summary>Gets the optional working directory used only to derive its basename.</summary>
    [JsonPropertyName("cwd")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the command Hook event name.</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    /// <summary>Gets the optional permission mode without retaining any requested command.</summary>
    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    /// <summary>Gets the tool name used only for an allow-listed category mapping.</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }
}
