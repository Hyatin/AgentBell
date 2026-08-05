using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Creates the opt-in M0 diagnostic logger from process environment variables.</summary>
public static class DiagnosticLoggerFactory
{
    /// <summary>Environment variable that enables Hook diagnostics when set to 1, true, yes, or on.</summary>
    public const string EnabledEnvironmentVariable = "AGENTBELL_HOOK_DIAGNOSTICS";

    /// <summary>Optional environment variable that overrides the diagnostic NDJSON path.</summary>
    public const string PathEnvironmentVariable = "AGENTBELL_HOOK_DIAGNOSTICS_PATH";

    /// <summary>Creates a diagnostic logger. Diagnostics are disabled by default.</summary>
    public static IDiagnosticLogger CreateFromEnvironment(AgentBellPathResolver? pathResolver = null)
    {
        var enabledValue = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        if (!IsEnabled(enabledValue))
        {
            return NullDiagnosticLogger.Instance;
        }

        try
        {
            var configuredPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return new JsonDiagnosticLogger(Path.GetFullPath(configuredPath));
            }

            var dataDirectory = (pathResolver ?? new AgentBellPathResolver()).DataDirectory;
            return new JsonDiagnosticLogger(Path.Combine(dataDirectory, "logs", "hook.ndjson"));
        }
        catch
        {
            return NullDiagnosticLogger.Instance;
        }
    }

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
}
