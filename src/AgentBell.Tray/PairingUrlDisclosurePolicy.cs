using AgentBell.Localization;

namespace AgentBell.Tray;

/// <summary>Centralizes the mandatory warning before a pairing credential enters the clipboard.</summary>
public static class PairingUrlDisclosurePolicy
{
    /// <summary>Gets whether explicit user confirmation is mandatory.</summary>
    public const bool RequiresConfirmation = true;

    /// <summary>Gets the warning shown before copying a credential-bearing pairing URL.</summary>
    public static string WarningText(IAppLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get("Pairing_UrlDisclosureWarning");
    }
}
