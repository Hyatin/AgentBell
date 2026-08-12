using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>
/// Defines the content-free loopback payload emitted by the Hook for an action-required event.
/// It cannot represent raw commands, tool input, prompts, paths, or source identifiers.
/// </summary>
public sealed record SanitizedActionRequiredEvent
{
    /// <summary>The discriminator accepted by the Desktop ingestion endpoint.</summary>
    public const string PermissionRequestEventType = "codex-permission-request";

    /// <summary>Gets the sanitized local event type.</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = PermissionRequestEventType;

    /// <summary>Gets the deterministic identifier derived only from irreversible hashes.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary>Gets the deterministic truncated session hash.</summary>
    [JsonPropertyName("sessionIdHash")]
    public string? SessionIdHash { get; init; }

    /// <summary>Gets the deterministic truncated turn hash.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets the deterministic truncated tool-use hash when supplied by Codex.</summary>
    [JsonPropertyName("toolUseIdHash")]
    public string? ToolUseIdHash { get; init; }

    /// <summary>Gets only the final working-directory segment.</summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>Gets the fixed action-required category.</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = AgentEventCategories.ActionRequired;

    /// <summary>Gets the fixed permission-required action.</summary>
    [JsonPropertyName("actionType")]
    public string ActionType { get; init; } = AgentActionTypes.PermissionRequired;

    /// <summary>Gets an allow-listed tool category.</summary>
    [JsonPropertyName("toolCategory")]
    public required string ToolCategory { get; init; }

    /// <summary>Gets when the Hook accepted the request.</summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Defines the content-free lifecycle correlation emitted after a supported Codex tool finishes.
/// It deliberately excludes tool input, tool output, commands, descriptions, paths, and source IDs.
/// </summary>
public sealed record SanitizedPostToolUseEvent
{
    /// <summary>The discriminator accepted by the Desktop ingestion endpoint.</summary>
    public const string PostToolUseEventType = "codex-post-tool-use";

    /// <summary>Gets the sanitized lifecycle event type.</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = PostToolUseEventType;

    /// <summary>Gets the deterministic truncated session hash.</summary>
    [JsonPropertyName("sessionIdHash")]
    public string? SessionIdHash { get; init; }

    /// <summary>Gets the deterministic truncated turn hash.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets the deterministic truncated tool-use hash when supplied by Codex.</summary>
    [JsonPropertyName("toolUseIdHash")]
    public string? ToolUseIdHash { get; init; }

    /// <summary>Gets an allow-listed tool category.</summary>
    [JsonPropertyName("toolCategory")]
    public required string ToolCategory { get; init; }

    /// <summary>Gets when the Hook accepted the lifecycle event.</summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }
}
