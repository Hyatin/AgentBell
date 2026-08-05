using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Defines the single source of truth for the M2 LAN protocol.</summary>
public static class AgentBellProtocol
{
    /// <summary>The current wire protocol version.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The compatible AgentBell server product version.</summary>
    public static string ServerVersion => AgentBellProduct.InformationalVersion;

    /// <summary>The authenticated WebSocket event path.</summary>
    public const string WebSocketPath = "/ws/v1/events";
}

/// <summary>Introduces an authenticated WebSocket server connection.</summary>
public sealed record HelloMessage
{
    /// <summary>Gets the protocol message type.</summary>
    [JsonPropertyName("type")]
    public string Type => "hello";

    /// <summary>Gets the wire protocol version.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion => AgentBellProtocol.ProtocolVersion;

    /// <summary>Gets the Desktop server version.</summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion => AgentBellProtocol.ServerVersion;

    /// <summary>Gets the display-only computer name.</summary>
    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>Gets the stable non-secret device identifier.</summary>
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    /// <summary>Gets the latest assigned event sequence.</summary>
    [JsonPropertyName("latestSequence")]
    public required long LatestSequence { get; init; }

    /// <summary>Gets the server wall-clock time.</summary>
    [JsonPropertyName("serverTime")]
    public required DateTimeOffset ServerTime { get; init; }
}

/// <summary>Carries one sanitized AgentBell event.</summary>
public sealed record EventMessage
{
    /// <summary>Gets the protocol message type.</summary>
    [JsonPropertyName("type")]
    public string Type => "event";

    /// <summary>Gets the sanitized event payload.</summary>
    [JsonPropertyName("payload")]
    public required AgentEvent Payload { get; init; }
}

/// <summary>Requests replay after a previously received sequence.</summary>
public sealed record ResumeMessage
{
    /// <summary>Gets the client message type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Gets the last sequence already retained by the client.</summary>
    [JsonPropertyName("lastSequence")]
    public long? LastSequence { get; init; }
}

/// <summary>Requests an application-level heartbeat response.</summary>
public sealed record PingMessage
{
    /// <summary>Gets the protocol message type.</summary>
    [JsonPropertyName("type")]
    public string Type => "ping";

    /// <summary>Gets the Unix timestamp echoed by the client.</summary>
    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}

/// <summary>Responds to an application-level heartbeat.</summary>
public sealed record PongMessage
{
    /// <summary>Gets the client message type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Gets the server timestamp being acknowledged.</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }
}

/// <summary>Reports a stable protocol error without exception details.</summary>
public sealed record ErrorMessage
{
    /// <summary>Gets the protocol message type.</summary>
    [JsonPropertyName("type")]
    public string Type => "error";

    /// <summary>Gets the stable, content-free error code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }
}

/// <summary>Describes the authenticated M2 LAN service.</summary>
public sealed record StatusResponse
{
    /// <summary>Gets the wire protocol version.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion => AgentBellProtocol.ProtocolVersion;

    /// <summary>Gets the Desktop server version.</summary>
    [JsonPropertyName("serverVersion")]
    public string ServerVersion => AgentBellProtocol.ServerVersion;

    /// <summary>Gets the display-only computer name.</summary>
    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>Gets the stable non-secret device identifier.</summary>
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    /// <summary>Gets the single bound RFC1918 address.</summary>
    [JsonPropertyName("lanAddress")]
    public required string LanAddress { get; init; }

    /// <summary>Gets the selected LAN port.</summary>
    [JsonPropertyName("lanPort")]
    public required int LanPort { get; init; }

    /// <summary>Gets the authenticated WebSocket path.</summary>
    [JsonPropertyName("webSocketPath")]
    public string WebSocketPath => AgentBellProtocol.WebSocketPath;

    /// <summary>Gets the latest assigned event sequence.</summary>
    [JsonPropertyName("latestSequence")]
    public required long LatestSequence { get; init; }

    /// <summary>Gets the current recent-event count.</summary>
    [JsonPropertyName("eventCount")]
    public required int EventCount { get; init; }
}
