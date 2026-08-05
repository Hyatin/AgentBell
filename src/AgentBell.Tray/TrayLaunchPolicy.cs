namespace AgentBell.Tray;

/// <summary>Maps bounded command-line switches to primary Tray startup behavior.</summary>
public static class TrayLaunchPolicy
{
    /// <summary>Gets whether this invocation only requests an existing instance to stop.</summary>
    public static bool IsShutdownRequest(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains("--shutdown", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets whether a newly created primary instance should show its main window.</summary>
    public static bool ShouldShowMainWindow(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return !IsShutdownRequest(arguments)
            && !arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase);
    }
}
