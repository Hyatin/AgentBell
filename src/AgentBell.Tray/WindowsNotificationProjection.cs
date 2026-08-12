using AgentBell.Localization;
using AgentBell.Contracts;
using AgentBell.Desktop;

namespace AgentBell.Tray;

/// <summary>Builds privacy-safe Windows completion notification text in the active UI language.</summary>
public static class WindowsNotificationProjection
{
    /// <summary>Creates generic text that never includes event content or identifiers.</summary>
    public static WindowsNotificationText Create(IAppLocalizer localizer) =>
        new(
            localizer.Get("WindowsNotification_Title"),
            localizer.Get("WindowsNotification_Body"));

    /// <summary>Creates content-free localized text for a sanitized event.</summary>
    public static WindowsNotificationText Create(
        IAppLocalizer localizer,
        AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(agentEvent);

        var (titleKey, bodyKey, genericBodyKey) = agentEvent.ActionType switch
        {
            AgentActionTypes.PermissionRequired =>
                ("PermissionRequired_Title", "PermissionRequired_Body", "PermissionRequired_BodyGeneric"),
            AgentActionTypes.InputRequired =>
                ("InputRequired_Title", "InputRequired_Body", "InputRequired_BodyGeneric"),
            AgentActionTypes.ConfirmationRequired =>
                ("ConfirmationRequired_Title", "ConfirmationRequired_Body", "ConfirmationRequired_BodyGeneric"),
            AgentActionTypes.AttentionRequired =>
                ("AttentionRequired_Title", "AttentionRequired_Body", "AttentionRequired_BodyGeneric"),
            _ => ("WindowsNotification_Title", "WindowsNotification_Body", "WindowsNotification_Body"),
        };
        var body = string.IsNullOrWhiteSpace(agentEvent.Project)
            ? localizer.Get(genericBodyKey)
            : localizer.Format(bodyKey, agentEvent.Project);
        return new WindowsNotificationText(localizer.Get(titleKey), body);
    }

    /// <summary>Applies local display settings without suppressing event synchronization.</summary>
    public static bool ShouldNotify(
        AgentEvent agentEvent,
        DesktopNotificationSettings settings) =>
        agentEvent.ResolvedAt is null
        && agentEvent.ActionType switch
        {
            AgentActionTypes.PermissionRequired =>
                settings.PermissionNotificationPolicy ==
                    PermissionNotificationPolicy.AlwaysNotify,
            AgentActionTypes.InputRequired or AgentActionTypes.ConfirmationRequired =>
                settings.NotifyActionRequired && settings.NotifyReplyAndConfirmationRequests,
            AgentActionTypes.AttentionRequired => settings.NotifyActionRequired,
            _ => settings.NotifyTaskCompletion,
        };
}

/// <summary>Represents localized, privacy-safe Windows notification text.</summary>
public sealed record WindowsNotificationText(string Title, string Body);
