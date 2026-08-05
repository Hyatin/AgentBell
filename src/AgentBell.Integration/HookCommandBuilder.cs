namespace AgentBell.Integration;

/// <summary>Builds the stable Codex Stop Hook commands for Windows.</summary>
public sealed class HookCommandBuilder
{
    /// <summary>The only supported Hook option.</summary>
    public const string StopHookOption = "--codex-stop-hook";

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
        var direct = $"{quotedExecutable} {StopHookOption}";
        var commandWindows = $"cmd.exe /d /s /c \"{direct}\"";
        return new HookCommands(path, direct, commandWindows);
    }
}

/// <summary>Contains the exact stable Hook command forms.</summary>
public sealed record HookCommands(
    string HookExecutablePath,
    string Command,
    string CommandWindows);
