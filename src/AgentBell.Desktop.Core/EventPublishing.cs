using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Publishes newly accepted events without exposing transport details to HTTP ingestion.</summary>
public interface IEventPublisher
{
    /// <summary>Queues one sanitized, sequenced event for real-time delivery.</summary>
    ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken);
}

/// <summary>Preserves M1 behavior when no real-time transport is available.</summary>
public sealed class NoOpEventPublisher : IEventPublisher
{
    private NoOpEventPublisher()
    {
    }

    /// <summary>Gets the stateless no-op publisher.</summary>
    public static NoOpEventPublisher Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Contains an immutable view of the recent in-memory event history.</summary>
public sealed record EventHistorySnapshot(
    IReadOnlyList<AgentEvent> Events,
    long LatestSequence,
    int EventCount);
