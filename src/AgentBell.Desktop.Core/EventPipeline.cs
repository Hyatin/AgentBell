using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Stable lifecycle states for one sanitized permission request.</summary>
public enum PermissionLifecycleState
{
    /// <summary>The request was observed while permission notifications were off.</summary>
    Observed,

    /// <summary>The request occurrence was persisted and published.</summary>
    Published,

    /// <summary>A matching lifecycle event completed the request lifecycle.</summary>
    Resolved,

    /// <summary>The bounded state cache evicted the lifecycle entry.</summary>
    Expired,
}

/// <summary>Contains only sanitized, content-free permission lifecycle state.</summary>
public sealed record PermissionLifecycle
{
    /// <summary>Gets the deterministic sanitized event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>Gets the irreversible session hash.</summary>
    public string? SessionIdHash { get; init; }

    /// <summary>Gets the irreversible turn hash.</summary>
    public string? TurnIdHash { get; init; }

    /// <summary>Gets the irreversible tool-use hash when available.</summary>
    public string? ToolUseIdHash { get; init; }

    /// <summary>Gets the allow-listed tool category.</summary>
    public required string ToolCategory { get; init; }

    /// <summary>Gets only the final working-directory segment.</summary>
    public string? Project { get; init; }

    /// <summary>Gets when Desktop received the sanitized request.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Gets the current lifecycle state.</summary>
    public required PermissionLifecycleState State { get; init; }
}

/// <summary>Serializes event acceptance, permission lifecycle correlation, and persistence.</summary>
public sealed class EventPipeline : IAsyncDisposable
{
    /// <summary>The maximum number of event identifiers retained for deduplication.</summary>
    public const int DeduplicationCapacity = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEventStore _eventStore;
    private readonly CodexEventTransformer _transformer;
    private readonly IEventPublisher _eventPublisher;
    private readonly DesktopNotificationSettingsState _notificationSettings;
    private readonly TimeProvider _timeProvider;
    private readonly LruEventIdSet _deduplication = new(DeduplicationCapacity);
    private readonly List<AgentEvent> _recentEvents = [];
    private readonly Dictionary<string, PermissionEntry> _permissions =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _permissionOrder = [];

    private long _sequence;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Initializes the pipeline with its store, transformer, publisher, settings, and clock.</summary>
    public EventPipeline(
        IEventStore eventStore,
        CodexEventTransformer transformer,
        IEventPublisher? eventPublisher = null,
        TimeProvider? timeProvider = null,
        DesktopNotificationSettingsState? notificationSettings = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
        _eventPublisher = eventPublisher ?? NoOpEventPublisher.Instance;
        _notificationSettings = notificationSettings ?? new DesktopNotificationSettingsState();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Restores deduplication, sequence, and already-published permission state.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            var loadResult = await _eventStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in loadResult.Events.OrderBy(item => item.Sequence))
            {
                _recentEvents.Add(item);
                _deduplication.TryAdd(item.EventId);
                _sequence = Math.Max(_sequence, item.Sequence);
                if (item.ActionType == AgentActionTypes.PermissionRequired)
                {
                    AddRestoredPermission(item);
                }
            }

            if (_recentEvents.Count > JsonEventStore.MaxRecentEvents)
            {
                _recentEvents.RemoveRange(
                    0,
                    _recentEvents.Count - JsonEventStore.MaxRecentEvents);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Accepts Stop, resolves matching permissions, then publishes normal completion.</summary>
    public async Task<EventAcceptanceResult> AcceptAsync(
        CodexStopHookPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var transformed = _transformer.TransformWithClassification(payload);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            await ResolveMatchingPermissionsLockedAsync(
                transformed.Event.ThreadIdHash,
                transformed.Event.TurnIdHash,
                toolUseIdHash: null,
                toolCategory: null,
                resolveAllInTurn: true,
                cancellationToken).ConfigureAwait(false);
            return await AcceptCandidateLockedAsync(
                transformed.Event,
                transformed.MatchedRuleId,
                transformed.ConfidenceBand,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Observes or immediately publishes one unique sanitized permission request.</summary>
    public async Task<EventAcceptanceResult> AcceptAsync(
        SanitizedActionRequiredEvent payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var candidate = _transformer.Transform(payload);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (_permissions.TryGetValue(candidate.EventId, out var existing)
                || _deduplication.Contains(candidate.EventId))
            {
                return new EventAcceptanceResult(
                    candidate,
                    IsDuplicate: true,
                    PersistenceSucceeded: true,
                    _recentEvents.Count,
                    ConfidenceBand: "structured",
                    PermissionState: existing?.Snapshot.State);
            }

            _deduplication.TryAdd(candidate.EventId);
            EnsurePermissionCapacityLocked();
            var shouldPublish = _notificationSettings.Current.PermissionNotificationPolicy ==
                PermissionNotificationPolicy.AlwaysNotify;
            var accepted = shouldPublish
                ? candidate with { Sequence = checked(++_sequence) }
                : candidate;
            var snapshot = new PermissionLifecycle
            {
                EventId = candidate.EventId,
                SessionIdHash = payload.SessionIdHash,
                TurnIdHash = payload.TurnIdHash,
                ToolUseIdHash = payload.ToolUseIdHash,
                ToolCategory = payload.ToolCategory,
                Project = payload.Project,
                ReceivedAt = _timeProvider.GetUtcNow(),
                State = shouldPublish
                    ? PermissionLifecycleState.Published
                    : PermissionLifecycleState.Observed,
            };
            var orderNode = _permissionOrder.AddLast(candidate.EventId);
            _permissions.Add(candidate.EventId, new PermissionEntry(snapshot, accepted, orderNode));

            var persistenceSucceeded = true;
            if (shouldPublish)
            {
                AddRecentEventLocked(accepted);
                persistenceSucceeded = await PersistAndPublishLockedAsync(
                    accepted,
                    cancellationToken).ConfigureAwait(false);
            }

            return new EventAcceptanceResult(
                accepted,
                IsDuplicate: false,
                persistenceSucceeded,
                _recentEvents.Count,
                ConfidenceBand: "structured",
                PermissionState: snapshot.State);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Correlates one supported PostToolUse lifecycle observation.</summary>
    public async Task<PermissionResolutionResult> ResolveAsync(
        SanitizedPostToolUseEvent payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            return await ResolveMatchingPermissionsLockedAsync(
                payload.SessionIdHash,
                payload.TurnIdHash,
                payload.ToolUseIdHash,
                payload.ToolCategory,
                resolveAllInTurn: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns one content-free permission lifecycle snapshot for deterministic tests.</summary>
    public async Task<PermissionLifecycle?> GetPermissionAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _permissions.TryGetValue(eventId, out var entry) ? entry.Snapshot : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns up to 100 events after a sequence, ordered by sequence.</summary>
    public async Task<EventHistorySnapshot> GetHistoryAsync(
        long lastSequence,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var events = _recentEvents
                .Where(item => item.Sequence > lastSequence)
                .OrderBy(item => item.Sequence)
                .Take(JsonEventStore.MaxRecentEvents)
                .ToArray();
            return new EventHistorySnapshot(events, _sequence, _recentEvents.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<EventAcceptanceResult> AcceptCandidateLockedAsync(
        AgentEvent candidate,
        string? matchedRuleId,
        string? confidenceBand,
        CancellationToken cancellationToken)
    {
        if (!_deduplication.TryAdd(candidate.EventId))
        {
            return new EventAcceptanceResult(
                candidate,
                IsDuplicate: true,
                PersistenceSucceeded: true,
                _recentEvents.Count,
                matchedRuleId,
                confidenceBand);
        }

        var accepted = candidate with { Sequence = checked(++_sequence) };
        AddRecentEventLocked(accepted);
        var persistenceSucceeded = await PersistAndPublishLockedAsync(
            accepted,
            cancellationToken).ConfigureAwait(false);
        return new EventAcceptanceResult(
            accepted,
            IsDuplicate: false,
            persistenceSucceeded,
            _recentEvents.Count,
            matchedRuleId,
            confidenceBand);
    }

    private async Task<PermissionResolutionResult> ResolveMatchingPermissionsLockedAsync(
        string? sessionIdHash,
        string? turnIdHash,
        string? toolUseIdHash,
        string? toolCategory,
        bool resolveAllInTurn,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnIdHash))
        {
            return PermissionResolutionResult.NotMatched(
                sessionIdHash,
                turnIdHash,
                toolUseIdHash,
                toolCategory);
        }

        var candidates = _permissions.Values
            .Where(item => item.Snapshot.State is
                PermissionLifecycleState.Observed or PermissionLifecycleState.Published)
            .Where(item => item.Snapshot.TurnIdHash == turnIdHash)
            .Where(item => sessionIdHash is null || item.Snapshot.SessionIdHash == sessionIdHash)
            .OrderBy(item => item.Snapshot.ReceivedAt)
            .ToArray();

        PermissionEntry[] matches;
        if (resolveAllInTurn)
        {
            matches = candidates;
        }
        else
        {
            var exact = string.IsNullOrWhiteSpace(toolUseIdHash)
                ? null
                : candidates.FirstOrDefault(item => item.Snapshot.ToolUseIdHash == toolUseIdHash);
            var fallback = exact is null
                ? candidates.FirstOrDefault(item =>
                    item.Snapshot.ToolUseIdHash is null
                    && item.Snapshot.ToolCategory == toolCategory)
                : null;
            matches = exact is not null ? [exact] : fallback is not null ? [fallback] : [];
        }

        if (matches.Length == 0)
        {
            return PermissionResolutionResult.NotMatched(
                sessionIdHash,
                turnIdHash,
                toolUseIdHash,
                toolCategory);
        }

        var observedCount = 0;
        var publishedCount = 0;
        var resolvedUpdates = new List<AgentEvent>();
        foreach (var entry in matches)
        {
            var previousState = entry.Snapshot.State;
            entry.Snapshot = entry.Snapshot with { State = PermissionLifecycleState.Resolved };
            if (previousState == PermissionLifecycleState.Observed)
            {
                observedCount++;
                continue;
            }

            publishedCount++;
            var index = _recentEvents.FindIndex(item => item.EventId == entry.Event.EventId);
            if (index < 0)
            {
                continue;
            }

            var resolved = _recentEvents[index] with
            {
                Sequence = checked(++_sequence),
                ResolvedAt = _timeProvider.GetUtcNow(),
            };
            _recentEvents.RemoveAt(index);
            _recentEvents.Add(resolved);
            entry.Event = resolved;
            resolvedUpdates.Add(resolved);
        }

        var persistenceSucceeded = true;
        if (resolvedUpdates.Count > 0)
        {
            persistenceSucceeded = await _eventStore.SaveAsync(
                _recentEvents.OrderBy(item => item.Sequence).ToArray(),
                cancellationToken).ConfigureAwait(false);
            foreach (var update in resolvedUpdates)
            {
                await TryPublishAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PermissionResolutionResult(
            Matched: true,
            ObservedResolved: observedCount,
            PublishedResolved: publishedCount,
            persistenceSucceeded,
            sessionIdHash,
            turnIdHash,
            toolUseIdHash,
            toolCategory);
    }

    private void AddRestoredPermission(AgentEvent item)
    {
        EnsurePermissionCapacityLocked();
        var snapshot = new PermissionLifecycle
        {
            EventId = item.EventId,
            SessionIdHash = item.ThreadIdHash,
            TurnIdHash = item.TurnIdHash,
            ToolUseIdHash = item.ToolUseIdHash,
            ToolCategory = item.ToolCategory,
            Project = item.Project,
            ReceivedAt = item.OccurredAt,
            State = item.ResolvedAt is null
                ? PermissionLifecycleState.Published
                : PermissionLifecycleState.Resolved,
        };
        var node = _permissionOrder.AddLast(item.EventId);
        _permissions[item.EventId] = new PermissionEntry(snapshot, item, node);
    }

    private void EnsurePermissionCapacityLocked()
    {
        while (_permissions.Count >= DeduplicationCapacity)
        {
            var oldest = _permissionOrder.First;
            if (oldest is null)
            {
                return;
            }

            _permissionOrder.RemoveFirst();
            if (_permissions.Remove(oldest.Value, out var evicted))
            {
                evicted.Snapshot = evicted.Snapshot with
                {
                    State = PermissionLifecycleState.Expired,
                };
            }
        }
    }

    private void AddRecentEventLocked(AgentEvent accepted)
    {
        _recentEvents.Add(accepted);
        if (_recentEvents.Count > JsonEventStore.MaxRecentEvents)
        {
            _recentEvents.RemoveAt(0);
        }
    }

    private async Task<bool> PersistAndPublishLockedAsync(
        AgentEvent accepted,
        CancellationToken cancellationToken)
    {
        var persisted = await _eventStore.SaveAsync(
            _recentEvents.OrderBy(item => item.Sequence).ToArray(),
            cancellationToken).ConfigureAwait(false);
        await TryPublishAsync(accepted, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    private async Task TryPublishAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _eventPublisher.PublishAsync(agentEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // LAN delivery is best-effort and cannot change the loopback HTTP contract.
        }
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("The event pipeline is not initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PermissionEntry(
        PermissionLifecycle snapshot,
        AgentEvent agentEvent,
        LinkedListNode<string> orderNode)
    {
        public PermissionLifecycle Snapshot { get; set; } = snapshot;

        public AgentEvent Event { get; set; } = agentEvent;

        public LinkedListNode<string> OrderNode { get; } = orderNode;
    }
}

/// <summary>Contains only sanitized event-acceptance metadata.</summary>
public sealed record EventAcceptanceResult(
    AgentEvent Event,
    bool IsDuplicate,
    bool PersistenceSucceeded,
    int EventCount,
    string? MatchedRuleId = null,
    string? ConfidenceBand = null,
    PermissionLifecycleState? PermissionState = null);

/// <summary>Contains content-free PostToolUse/Stop resolution metadata.</summary>
public sealed record PermissionResolutionResult(
    bool Matched,
    int ObservedResolved,
    int PublishedResolved,
    bool PersistenceSucceeded,
    string? SessionIdHash,
    string? TurnIdHash,
    string? ToolUseIdHash,
    string? ToolCategory)
{
    /// <summary>Creates a safe result when no permission could be correlated.</summary>
    public static PermissionResolutionResult NotMatched(
        string? sessionIdHash,
        string? turnIdHash,
        string? toolUseIdHash,
        string? toolCategory) =>
        new(false, 0, 0, true, sessionIdHash, turnIdHash, toolUseIdHash, toolCategory);
}
