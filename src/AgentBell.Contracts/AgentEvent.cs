using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Represents one sanitized, locally persisted AgentBell event.</summary>
public sealed record AgentEvent
{
    /// <summary>Gets the deterministic event identifier when source identifiers are available.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary>Gets the source agent name.</summary>
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    /// <summary>Gets the normalized event status.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Gets whether the event represents completion or requires user action.</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = AgentEventCategories.Completion;

    /// <summary>Gets the normalized user action, or <c>none</c> for completion.</summary>
    [JsonPropertyName("actionType")]
    public string ActionType { get; init; } = AgentActionTypes.None;

    /// <summary>Gets the allow-listed tool category without retaining tool input.</summary>
    [JsonPropertyName("toolCategory")]
    public string ToolCategory { get; init; } = AgentToolCategories.None;

    /// <summary>Gets the user-facing event title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Gets only the final working-directory segment, never the complete path.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>Gets the normalized, Unicode-safe truncated assistant summary.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>Gets a deterministic truncated SHA-256 session reference.</summary>
    [JsonPropertyName("threadIdHash")]
    public string? ThreadIdHash { get; init; }

    /// <summary>Gets a deterministic truncated SHA-256 turn reference.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets a deterministic truncated tool-call reference for lifecycle correlation.</summary>
    [JsonPropertyName("toolUseIdHash")]
    public string? ToolUseIdHash { get; init; }

    /// <summary>Gets when AgentBell accepted the source event.</summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Gets the process-monotonic sequence, restored from persisted history on restart.</summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    /// <summary>
    /// Gets when an already-published action was resolved. A resolved action remains in sanitized
    /// history but must not produce or retain an active user notification.
    /// </summary>
    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>Stable event categories added without changing protocol version 1.</summary>
public static class AgentEventCategories
{
    /// <summary>A Codex turn completed normally.</summary>
    public const string Completion = "completion";

    /// <summary>Codex is waiting for user action.</summary>
    public const string ActionRequired = "action_required";
}

/// <summary>Stable action-required subtypes.</summary>
public static class AgentActionTypes
{
    /// <summary>No user action is associated with the event.</summary>
    public const string None = "none";

    /// <summary>Codex is waiting for a permission decision.</summary>
    public const string PermissionRequired = "permission_required";

    /// <summary>Codex is waiting for a reply or selection.</summary>
    public const string InputRequired = "input_required";

    /// <summary>Codex is waiting for confirmation.</summary>
    public const string ConfirmationRequired = "confirmation_required";

    /// <summary>Codex is blocked and needs attention.</summary>
    public const string AttentionRequired = "attention_required";
}

/// <summary>Allow-listed tool categories that are safe to synchronize.</summary>
public static class AgentToolCategories
{
    /// <summary>No tool is associated with the event.</summary>
    public const string None = "none";

    /// <summary>A command execution request.</summary>
    public const string Command = "command";

    /// <summary>A file edit or write request.</summary>
    public const string FileChange = "file_change";

    /// <summary>A network access request.</summary>
    public const string NetworkAccess = "network_access";

    /// <summary>An external MCP tool request.</summary>
    public const string ExternalTool = "external_tool";

    /// <summary>A computer-control request.</summary>
    public const string ComputerControl = "computer_control";

    /// <summary>An unrecognized tool category.</summary>
    public const string Other = "other";
}
