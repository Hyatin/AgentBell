using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Integration;

namespace AgentBell.Tray;

/// <summary>Builds UI text from content-free state and never accepts a pairing token.</summary>
public static class TrayStatusProjection
{
    /// <summary>Creates the status values rendered by the main window.</summary>
    public static IReadOnlyDictionary<string, string> Create(
        AgentBellRuntimeSnapshot snapshot,
        CodexIntegrationResult integration,
        StartupRegistrationResult startup,
        string androidApkPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(startup);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = AgentBellProduct.InformationalVersion,
            ["hook"] = snapshot.LocalHookService.ToString(),
            ["lan"] = snapshot.LanService.ToString(),
            ["endpoint"] = snapshot.LanAddress is null || snapshot.LanPort is null
                ? "—"
                : $"{snapshot.LanAddress}:{snapshot.LanPort}",
            ["clients"] = snapshot.WebSocketClientCount.ToString(),
            ["lastEvent"] = snapshot.LastEventTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
            ["sequence"] = snapshot.LatestSequence.ToString(),
            ["startup"] = startup.State.ToString(),
            ["integration"] = integration.State.ToString(),
            ["apk"] = androidApkPath,
        };
    }
}
