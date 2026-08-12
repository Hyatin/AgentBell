namespace AgentBell.Integration;

/// <summary>Builds stable Stop, PermissionRequest, and PostToolUse Hook commands for Windows.</summary>
public sealed class HookCommandBuilder
{
    /// <summary>The Stop Hook option.</summary>
    public const string StopHookOption = "--codex-stop-hook";

    /// <summary>The PermissionRequest Hook option.</summary>
    public const string PermissionRequestHookOption = "--codex-permission-request-hook";

    /// <summary>The PostToolUse Hook option.</summary>
    public const string PostToolUseHookOption = "--codex-post-tool-use-hook";

    /// <summary>Builds direct and commandWindows values for an absolute Hook executable path.</summary>
    public HookCommands Build(string hookExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hookExecutablePath);
        var path = Path.GetFullPath(hookExecutablePath);
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(
                Path.GetFileName(path),
                "AgentBell.Hook.exe",
                StringComparison.OrdinalIgnoreCase)
            || path.Contains('"', StringComparison.Ordinal)
            || path.Contains('%', StringComparison.Ordinal))
        {
            throw new ArgumentException("The Hook path cannot be represented safely.", nameof(hookExecutablePath));
        }

        var quotedExecutable = $"\"{path}\"";
        return new HookCommands(
            path,
            CreateDefinition(
                "Stop",
                StopHookOption,
                "Sending completion to AgentBell",
                quotedExecutable),
            CreateDefinition(
                "PermissionRequest",
                PermissionRequestHookOption,
                "Sending permission request to AgentBell",
                quotedExecutable),
            CreateDefinition(
                "PostToolUse",
                PostToolUseHookOption,
                "Resolving AgentBell permission request",
                quotedExecutable));
    }

    private static HookCommandDefinition CreateDefinition(
        string eventName,
        string option,
        string statusMessage,
        string quotedExecutable)
    {
        var direct = $"{quotedExecutable} {option}";
        return new HookCommandDefinition(
            eventName,
            option,
            direct,
            $"cmd.exe /d /s /c \"{direct}\"",
            statusMessage);
    }
}

/// <summary>Contains both exact managed Hook command forms.</summary>
public sealed record HookCommands(
    string HookExecutablePath,
    HookCommandDefinition Stop,
    HookCommandDefinition PermissionRequest,
    HookCommandDefinition PostToolUse)
{
    /// <summary>Compatibility alias for the Stop command.</summary>
    public string Command => Stop.Command;

    /// <summary>Compatibility alias for the Stop commandWindows value.</summary>
    public string CommandWindows => Stop.CommandWindows;

    /// <summary>Gets all three required definitions.</summary>
    public IReadOnlyList<HookCommandDefinition> All => [Stop, PermissionRequest, PostToolUse];
}

/// <summary>Defines one managed Codex event-group command.</summary>
public sealed record HookCommandDefinition(
    string EventName,
    string Option,
    string Command,
    string CommandWindows,
    string StatusMessage);
