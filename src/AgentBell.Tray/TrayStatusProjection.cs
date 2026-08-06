using System.Globalization;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Integration;
using AgentBell.Localization;

namespace AgentBell.Tray;

/// <summary>Builds UI text from content-free state and never accepts a pairing token.</summary>
public static class TrayStatusProjection
{
    /// <summary>Creates the status values rendered by the main window.</summary>
    public static IReadOnlyDictionary<string, string> Create(
        AgentBellRuntimeSnapshot snapshot,
        CodexIntegrationResult integration,
        StartupRegistrationResult startup,
        string androidApkPath,
        IAppLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(localizer);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = AgentBellProduct.InformationalVersion,
            ["hook"] = LocalizeRuntimeStatus(snapshot.LocalHookService, localizer),
            ["lan"] = LocalizeRuntimeStatus(snapshot.LanService, localizer),
            ["endpoint"] = snapshot.LanAddress is null || snapshot.LanPort is null
                ? "—"
                : string.Create(CultureInfo.InvariantCulture, $"{snapshot.LanAddress}:{snapshot.LanPort}"),
            ["clients"] = snapshot.WebSocketClientCount.ToString(CultureInfo.InvariantCulture),
            ["lastEvent"] = snapshot.LastEventTime?.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture) ?? "—",
            ["sequence"] = snapshot.LatestSequence.ToString(CultureInfo.InvariantCulture),
            ["startup"] = startup.State switch
            {
                StartupRegistrationState.Enabled => localizer.Get("Common_Enabled"),
                StartupRegistrationState.Disabled => localizer.Get("Common_Disabled"),
                _ => localizer.Get("Common_Error"),
            },
            ["integration"] = integration.State switch
            {
                CodexIntegrationState.Installed => localizer.Get("Status_IntegrationInstalled"),
                CodexIntegrationState.Missing => localizer.Get("Status_IntegrationMissing"),
                CodexIntegrationState.NeedsRepair => localizer.Get("Status_IntegrationNeedsRepair"),
                CodexIntegrationState.NeedsManualReview => localizer.Get("Status_IntegrationNeedsManualReview"),
                _ => localizer.Get("Common_Unknown"),
            },
            ["apk"] = androidApkPath,
        };
    }

    private static string LocalizeRuntimeStatus(
        RuntimeServiceStatus status,
        IAppLocalizer localizer) => status switch
        {
            RuntimeServiceStatus.Running => localizer.Get("Common_Running"),
            RuntimeServiceStatus.Stopped => localizer.Get("Common_Stopped"),
            RuntimeServiceStatus.Available => localizer.Get("Common_Available"),
            RuntimeServiceStatus.Unavailable => localizer.Get("Common_Unavailable"),
            _ => localizer.Get("Common_Error"),
        };
}
