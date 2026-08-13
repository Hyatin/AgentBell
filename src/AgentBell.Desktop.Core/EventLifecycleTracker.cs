using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Stable states for one bounded provider-scoped lifecycle entry.</summary>
public enum EventLifecycleState
{
    /// <summary>The event is tracked without a deliverable event.</summary>
    Tracked,

    /// <summary>The event occurrence was persisted and published.</summary>
    Delivered,

    /// <summary>A matching lifecycle directive resolved the entry.</summary>
    Resolved,

    /// <summary>The bounded state cache evicted the entry.</summary>
    Expired,
}

/// <summary>Contains only sanitized provider-scoped lifecycle state.</summary>
public sealed record EventLifecycleSnapshot
{
    /// <summary>Gets the provider namespace.</summary>
    public required ProviderId ProviderId { get; init; }

    /// <summary>Gets the normalized event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets the irreversible session hash.</summary>
    public string? SessionIdHash { get; init; }

    /// <summary>Gets the irreversible turn hash.</summary>
    public string? TurnIdHash { get; init; }

    /// <summary>Gets the irreversible tool-use hash.</summary>
    public string? ToolUseIdHash { get; init; }

    /// <summary>Gets the allow-listed tool category.</summary>
    public required string ToolCategory { get; init; }

    /// <summary>Gets a bounded display-only project label.</summary>
    public string? Project { get; init; }

    /// <summary>Gets when the entry was received.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Gets the current lifecycle state.</summary>
    public required EventLifecycleState State { get; init; }
}

internal sealed record EventLifecycleRegistration(
    ProviderId ProviderId,
    string EventId,
    string? SessionIdHash,
    string? TurnIdHash,
    string? ToolUseIdHash,
    string ToolCategory,
    string? Project,
    DateTimeOffset ReceivedAt,
    EventLifecycleState State,
    AgentEvent? DeliverableEvent);

internal sealed class EventLifecycleTracker
{
    private readonly int _capacity;
    private readonly Dictionary<LifecycleKey, Entry> _entries = [];
    private readonly LinkedList<LifecycleKey> _order = [];

    public EventLifecycleTracker(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public EventLifecycleSnapshot? Get(ProviderId providerId, string eventId) =>
        _entries.TryGetValue(new LifecycleKey(providerId, eventId), out var entry)
            ? entry.Snapshot
            : null;

    public void Register(EventLifecycleRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = new LifecycleKey(registration.ProviderId, registration.EventId);
        if (_entries.Remove(key, out var existing))
        {
            _order.Remove(existing.OrderNode);
        }

        EnsureCapacity();
        var node = _order.AddLast(key);
        _entries[key] = new Entry(
            new EventLifecycleSnapshot
            {
                ProviderId = registration.ProviderId,
                EventId = registration.EventId,
                SessionIdHash = registration.SessionIdHash,
                TurnIdHash = registration.TurnIdHash,
                ToolUseIdHash = registration.ToolUseIdHash,
                ToolCategory = registration.ToolCategory,
                Project = registration.Project,
                ReceivedAt = registration.ReceivedAt,
                State = registration.State,
            },
            registration.DeliverableEvent,
            node);
    }

    public IReadOnlyList<EventLifecycleMatch> Resolve(
        ProviderId providerId,
        LifecycleDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (directive.Kind is not LifecycleDirectiveKind.ResolveOne
            and not LifecycleDirectiveKind.ResolveAllInTurn)
        {
            return [];
        }

        var candidates = _entries.Values
            .Where(entry => entry.Snapshot.ProviderId == providerId)
            .Where(entry => entry.Snapshot.State is EventLifecycleState.Tracked or EventLifecycleState.Delivered)
            .Where(entry => entry.Snapshot.TurnIdHash == directive.TurnIdHash)
            .Where(entry => directive.SessionIdHash is null
                || entry.Snapshot.SessionIdHash == directive.SessionIdHash)
            .OrderBy(entry => entry.Snapshot.ReceivedAt)
            .ToArray();
        Entry[] matches;
        if (directive.Kind == LifecycleDirectiveKind.ResolveAllInTurn)
        {
            matches = candidates;
        }
        else
        {
            var exact = directive.ToolUseIdHash is null
                ? null
                : candidates.FirstOrDefault(entry => entry.Snapshot.ToolUseIdHash == directive.ToolUseIdHash);
            var fallback = exact is null
                ? candidates.FirstOrDefault(entry =>
                    entry.Snapshot.ToolUseIdHash is null
                    && entry.Snapshot.ToolCategory == directive.ToolCategory)
                : null;
            matches = exact is not null ? [exact] : fallback is not null ? [fallback] : [];
        }

        var resolved = new List<EventLifecycleMatch>(matches.Length);
        foreach (var entry in matches)
        {
            var previousState = entry.Snapshot.State;
            entry.Snapshot = entry.Snapshot with { State = EventLifecycleState.Resolved };
            resolved.Add(new EventLifecycleMatch(previousState, entry.Snapshot, entry.DeliverableEvent));
        }

        return resolved;
    }

    public void UpdateDeliverable(
        ProviderId providerId,
        string eventId,
        AgentEvent deliverableEvent)
    {
        if (_entries.TryGetValue(new LifecycleKey(providerId, eventId), out var entry))
        {
            entry.DeliverableEvent = deliverableEvent;
        }
    }

    private void EnsureCapacity()
    {
        while (_entries.Count >= _capacity)
        {
            var oldest = _order.First;
            if (oldest is null)
            {
                return;
            }

            _order.RemoveFirst();
            if (_entries.Remove(oldest.Value, out var evicted))
            {
                evicted.Snapshot = evicted.Snapshot with { State = EventLifecycleState.Expired };
            }
        }
    }

    private sealed class Entry(
        EventLifecycleSnapshot snapshot,
        AgentEvent? deliverableEvent,
        LinkedListNode<LifecycleKey> orderNode)
    {
        public EventLifecycleSnapshot Snapshot { get; set; } = snapshot;

        public AgentEvent? DeliverableEvent { get; set; } = deliverableEvent;

        public LinkedListNode<LifecycleKey> OrderNode { get; } = orderNode;
    }

    private readonly record struct LifecycleKey(ProviderId ProviderId, string EventId);
}

internal sealed record EventLifecycleMatch(
    EventLifecycleState PreviousState,
    EventLifecycleSnapshot Snapshot,
    AgentEvent? DeliverableEvent);
