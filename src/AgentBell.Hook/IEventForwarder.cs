namespace AgentBell.Hook;

/// <summary>Forwards a validated raw Codex event to the local desktop endpoint.</summary>
public interface IEventForwarder
{
    /// <summary>Forwards the raw JSON using the supplied cancellation token.</summary>
    /// <param name="rawJson">The exact validated Codex JSON argument.</param>
    /// <param name="cancellationToken">Cancels the I/O operation.</param>
    /// <returns>A stable forwarding result.</returns>
    Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken);
}

