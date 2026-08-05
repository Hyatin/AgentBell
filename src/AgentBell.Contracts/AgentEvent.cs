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

    /// <summary>Gets when AgentBell accepted the source event.</summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Gets the process-monotonic sequence, restored from persisted history on restart.</summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }
}
