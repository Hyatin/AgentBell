using Microsoft.Win32;

namespace AgentBell.Tray;

/// <summary>Describes the current-user AgentBell login-start state.</summary>
public enum StartupRegistrationState
{
    /// <summary>The exact current Tray command is registered.</summary>
    Enabled,

    /// <summary>No exact current Tray command is registered.</summary>
    Disabled,

    /// <summary>The registry operation failed.</summary>
    Error,
}

/// <summary>Contains a stable startup operation result.</summary>
public sealed record StartupRegistrationResult(
    bool Success,
    StartupRegistrationState State,
    string Code);

/// <summary>Abstracts the single HKCU Run value for deterministic tests.</summary>
public interface IStartupValueStore
{
    /// <summary>Reads the AgentBell Run value.</summary>
    string? Read();

    /// <summary>Writes the AgentBell Run value.</summary>
    void Write(string value);

    /// <summary>Deletes the AgentBell Run value if present.</summary>
    void Delete();
}

/// <summary>Uses HKCU without administrator privileges.</summary>
public sealed class WindowsRunStartupValueStore : IStartupValueStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentBell";

    /// <inheritdoc />
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    /// <inheritdoc />
    public void Write(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("The current-user Run key is unavailable.");
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Manages the exact AgentBell current-user login-start command idempotently.</summary>
public sealed class StartupRegistration
{
    private readonly IStartupValueStore _store;
    private readonly string _expectedCommand;

    /// <summary>Initializes registration for an absolute AgentBell.Tray.exe path.</summary>
    public StartupRegistration(string trayExecutablePath, IStartupValueStore? store = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trayExecutablePath);
        var path = Path.GetFullPath(trayExecutablePath);
        if (!string.Equals(
            Path.GetFileName(path),
            "AgentBell.Tray.exe",
            StringComparison.OrdinalIgnoreCase)
            || path.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The Tray path cannot be registered safely.", nameof(trayExecutablePath));
        }

        _expectedCommand = $"\"{path}\" --startup";
        _store = store ?? new WindowsRunStartupValueStore();
    }

    /// <summary>Gets the exact Run value for tests and installer verification.</summary>
    public string ExpectedCommand => _expectedCommand;

    /// <summary>Enables login start idempotently.</summary>
    public StartupRegistrationResult Enable()
    {
        try
        {
            if (!string.Equals(_store.Read(), _expectedCommand, StringComparison.Ordinal))
            {
                _store.Write(_expectedCommand);
            }

            return new StartupRegistrationResult(true, StartupRegistrationState.Enabled, "enabled");
        }
        catch
        {
            return new StartupRegistrationResult(false, StartupRegistrationState.Error, "startup_write_failed");
        }
    }

    /// <summary>Disables login start idempotently.</summary>
    public StartupRegistrationResult Disable()
    {
        try
        {
            _store.Delete();
            return new StartupRegistrationResult(true, StartupRegistrationState.Disabled, "disabled");
        }
        catch
        {
            return new StartupRegistrationResult(false, StartupRegistrationState.Error, "startup_delete_failed");
        }
    }

    /// <summary>Returns whether the exact current Tray path is enabled.</summary>
    public StartupRegistrationResult Status()
    {
        try
        {
            var enabled = string.Equals(_store.Read(), _expectedCommand, StringComparison.Ordinal);
            return new StartupRegistrationResult(
                true,
                enabled ? StartupRegistrationState.Enabled : StartupRegistrationState.Disabled,
                enabled ? "enabled" : "disabled");
        }
        catch
        {
            return new StartupRegistrationResult(false, StartupRegistrationState.Error, "startup_read_failed");
        }
    }
}
