namespace AgentBell.Tray;

/// <summary>Centralizes the mandatory warning before a pairing credential enters the clipboard.</summary>
public static class PairingUrlDisclosurePolicy
{
    /// <summary>Gets whether explicit user confirmation is mandatory.</summary>
    public const bool RequiresConfirmation = true;

    /// <summary>Gets the warning shown before copying a credential-bearing pairing URL.</summary>
    public const string WarningText =
        "配对 URL 包含配对凭据。不要发送给不可信人员，并且只在可信局域网使用。是否仍要复制？";
}
