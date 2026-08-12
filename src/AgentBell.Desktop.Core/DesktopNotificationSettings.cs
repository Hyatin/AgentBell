namespace AgentBell.Desktop;

/// <summary>Controls whether PermissionRequest occurrence notifications are published.</summary>
public enum PermissionNotificationPolicy
{
    /// <summary>Keep the Hook lifecycle observable only through sanitized diagnostics.</summary>
    Off,

    /// <summary>Publish every unique PermissionRequest occurrence immediately.</summary>
    AlwaysNotify,
}

/// <summary>Stable persisted values for permission-request notification policy.</summary>
public static class PermissionNotificationPolicyValues
{
    /// <summary>The persisted value for the safe default.</summary>
    public const string Off = "off";

    /// <summary>The persisted value for immediate occurrence notifications.</summary>
    public const string AlwaysNotify = "always_notify";

    /// <summary>Maps a process-local policy to its stable persisted representation.</summary>
    public static string ToPersistedValue(PermissionNotificationPolicy value) => value switch
    {
        PermissionNotificationPolicy.AlwaysNotify => AlwaysNotify,
        _ => Off,
    };

    /// <summary>Parses a persisted value, safely defaulting unknown values to off.</summary>
    public static PermissionNotificationPolicy Parse(string? value) => value switch
    {
        AlwaysNotify => PermissionNotificationPolicy.AlwaysNotify,
        _ => PermissionNotificationPolicy.Off,
    };

    /// <summary>Returns whether a value is one of the two supported persisted values.</summary>
    public static bool IsSupported(string? value) => value is Off or AlwaysNotify;
}

/// <summary>Controls only local Windows notification display and Stop-response classification.</summary>
public sealed record DesktopNotificationSettings
{
    /// <summary>Gets whether completion balloons are enabled.</summary>
    public bool NotifyTaskCompletion { get; init; } = true;

    /// <summary>Gets whether any action-required balloons are enabled.</summary>
    public bool NotifyActionRequired { get; init; } = true;

    /// <summary>Gets the explicit permission-request occurrence notification policy.</summary>
    public PermissionNotificationPolicy PermissionNotificationPolicy { get; init; } =
        PermissionNotificationPolicy.Off;

    /// <summary>Gets whether reply and confirmation balloons are enabled.</summary>
    public bool NotifyReplyAndConfirmationRequests { get; init; } = true;

    /// <summary>Gets whether Stop responses receive conservative action classification.</summary>
    public bool DetectQuestionsInCompletedResponses { get; init; } = true;
}

/// <summary>Provides an atomic process-local settings snapshot to the ingestion pipeline.</summary>
public sealed class DesktopNotificationSettingsState
{
    private DesktopNotificationSettings _current = new();

    /// <summary>Gets the current immutable settings snapshot.</summary>
    public DesktopNotificationSettings Current => Volatile.Read(ref _current);

    /// <summary>Replaces the current snapshot atomically.</summary>
    public void Update(DesktopNotificationSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref _current, value);
    }
}
