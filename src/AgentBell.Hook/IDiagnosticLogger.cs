namespace AgentBell.Hook;

/// <summary>Records opt-in, content-free Hook diagnostics.</summary>
public interface IDiagnosticLogger
{
    /// <summary>Records one sanitized event. Implementations must not expose exception details.</summary>
    void Record(HookDiagnosticEvent diagnosticEvent);
}

