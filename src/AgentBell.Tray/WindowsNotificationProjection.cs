using AgentBell.Localization;

namespace AgentBell.Tray;

/// <summary>Builds privacy-safe Windows completion notification text in the active UI language.</summary>
public static class WindowsNotificationProjection
{
    /// <summary>Creates generic text that never includes event content or identifiers.</summary>
    public static WindowsNotificationText Create(IAppLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return new WindowsNotificationText(
            localizer.Get("WindowsNotification_Title"),
            localizer.Get("WindowsNotification_Body"));
    }
}

/// <summary>Represents localized, privacy-safe Windows notification text.</summary>
public sealed record WindowsNotificationText(string Title, string Body);
