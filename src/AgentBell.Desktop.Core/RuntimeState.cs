namespace AgentBell.Desktop;

using AgentBell.Contracts;

/// <summary>Describes one public, content-free runtime service state.</summary>
public enum RuntimeServiceStatus
{
    /// <summary>The service is not running.</summary>
    Stopped,

    /// <summary>The loopback service is accepting Hook events.</summary>
    Running,

    /// <summary>The optional LAN service is available.</summary>
    Available,

    /// <summary>The optional LAN service is unavailable while loopback may remain available.</summary>
    Unavailable,

    /// <summary>The service failed with a stable content-free result code.</summary>
    Error,
}

/// <summary>Contains the status fields safe to render in the Tray UI or diagnostics.</summary>
public sealed record AgentBellRuntimeSnapshot
{
    /// <summary>Gets the local Hook listener state.</summary>
    public required RuntimeServiceStatus LocalHookService { get; init; }

    /// <summary>Gets the LAN listener state.</summary>
    public required RuntimeServiceStatus LanService { get; init; }

    /// <summary>Gets a stable local-listener result code.</summary>
    public required string LocalResultCode { get; init; }

    /// <summary>Gets a stable LAN result code.</summary>
    public required string LanResultCode { get; init; }

    /// <summary>Gets the selected RFC1918 address for local display only.</summary>
    public string? LanAddress { get; init; }

    /// <summary>Gets the selected LAN port.</summary>
    public int? LanPort { get; init; }

    /// <summary>Gets the number of authenticated WebSocket clients.</summary>
    public int WebSocketClientCount { get; init; }

    /// <summary>Gets the latest persisted or in-memory sequence.</summary>
    public long LatestSequence { get; init; }

    /// <summary>Gets the recent event count.</summary>
    public int EventCount { get; init; }

    /// <summary>Gets the last sanitized event time.</summary>
    public DateTimeOffset? LastEventTime { get; init; }

    /// <summary>Gets the sanitized recent events in ascending sequence order.</summary>
    public IReadOnlyList<AgentEvent> RecentEvents { get; init; } = [];

    /// <summary>Gets current local Windows notification settings.</summary>
    public DesktopNotificationSettings NotificationSettings { get; init; } = new();

    /// <summary>Gets the pairing QR path, which never contains the token itself.</summary>
    public string? PairingQrCodePath { get; init; }

    /// <summary>Gets whether a pairing QR was generated successfully.</summary>
    public bool PairingQrAvailable { get; init; }
}
