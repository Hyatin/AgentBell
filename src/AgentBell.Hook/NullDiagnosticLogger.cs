namespace AgentBell.Hook;

/// <summary>Discards diagnostics when the opt-in environment variable is not enabled.</summary>
public sealed class NullDiagnosticLogger : IDiagnosticLogger
{
    private NullDiagnosticLogger()
    {
    }

    /// <summary>Gets the stateless singleton instance.</summary>
    public static NullDiagnosticLogger Instance { get; } = new();

    /// <inheritdoc />
    public void Record(HookDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
    }
}

