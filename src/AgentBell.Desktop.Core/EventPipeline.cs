using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Serializes provider-neutral event acceptance, lifecycle correlation, and persistence.</summary>
public sealed class EventPipeline : IAsyncDisposable
{
    /// <summary>The maximum number of identifiers retained for deduplication and lifecycle state.</summary>
    public const int DeduplicationCapacity = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEventStore _eventStore;
    private readonly AgentEventProjector _projector;
    private readonly IEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly LruEventIdSet _deduplication = new(DeduplicationCapacity);
    private readonly List<AgentEvent> _recentEvents = [];
    private readonly EventLifecycleTracker _lifecycle = new(DeduplicationCapacity);

    private long _sequence;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Initializes the generic pipeline with persistence, projection, publishing, and time.</summary>
    public EventPipeline(
        IEventStore eventStore,
        AgentEventProjector? projector = null,
        IEventPublisher? eventPublisher = null,
        TimeProvider? timeProvider = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _projector = projector ?? new AgentEventProjector();
        _eventPublisher = eventPublisher ?? NoOpEventPublisher.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Restores deduplication, sequence, and deliverable lifecycle state.</summary>
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
                if (_projector.TryCreateRestoredLifecycle(item, out var registration))
                {
                    _lifecycle.Register(registration!);
                }
            }

            if (_recentEvents.Count > JsonEventStore.MaxRecentEvents)
            {
                _recentEvents.RemoveRange(0, _recentEvents.Count - JsonEventStore.MaxRecentEvents);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Executes one fully sanitized provider-neutral submission.</summary>
    public async Task<EventPipelineResult> AcceptAsync(
        EventPipelineSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var resolution = await ExecuteResolutionLockedAsync(
                submission.ProviderId,
                submission.Lifecycle,
                cancellationToken).ConfigureAwait(false);

            var normalizedEvent = submission.NormalizedEvent;
            if (normalizedEvent is null)
            {
                return new EventPipelineResult(
                    null,
                    null,
                    IsDuplicate: false,
                    resolution.PersistenceSucceeded,
                    _recentEvents.Count,
                    null,
                    resolution);
            }

            var candidate = _projector.Project(normalizedEvent, submission.Notification, sequence: 0);
            if (!_deduplication.TryAdd(normalizedEvent.EventId))
            {
                return new EventPipelineResult(
                    candidate,
                    normalizedEvent,
                    IsDuplicate: true,
                    PersistenceSucceeded: true,
                    _recentEvents.Count,
                    _lifecycle.Get(submission.ProviderId, normalizedEvent.EventId)?.State,
                    resolution);
            }

            AgentEvent? accepted = null;
            if (candidate is not null)
            {
                accepted = candidate with { Sequence = checked(++_sequence) };
            }

            EventLifecycleState? lifecycleState = null;
            if (submission.Lifecycle.Kind == LifecycleDirectiveKind.Register)
            {
                lifecycleState = accepted is null
                    ? EventLifecycleState.Tracked
                    : EventLifecycleState.Delivered;
                _lifecycle.Register(new EventLifecycleRegistration(
                    submission.ProviderId,
                    normalizedEvent.EventId,
                    normalizedEvent.SessionIdHash,
                    normalizedEvent.TurnIdHash,
                    normalizedEvent.ToolUseIdHash,
                    normalizedEvent.ToolCategory,
                    normalizedEvent.Project,
                    _timeProvider.GetUtcNow(),
                    lifecycleState.Value,
                    accepted));
            }

            var persistenceSucceeded = true;
            if (accepted is not null)
            {
                AddRecentEventLocked(accepted);
                persistenceSucceeded = await PersistAndPublishLockedAsync(
                    accepted,
                    cancellationToken).ConfigureAwait(false);
            }

            return new EventPipelineResult(
                accepted,
                normalizedEvent,
                IsDuplicate: false,
                persistenceSucceeded,
                _recentEvents.Count,
                lifecycleState,
                resolution);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns one content-free provider-scoped lifecycle snapshot.</summary>
    public async Task<EventLifecycleSnapshot?> GetLifecycleAsync(
        ProviderId providerId,
        string eventId,
        CancellationToken cancellationToken)
    {
        _ = providerId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            return _lifecycle.Get(providerId, eventId);
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

    private async Task<LifecycleResolutionResult> ExecuteResolutionLockedAsync(
        ProviderId providerId,
        LifecycleDirective directive,
        CancellationToken cancellationToken)
    {
        if (directive.Kind is not LifecycleDirectiveKind.ResolveOne
            and not LifecycleDirectiveKind.ResolveAllInTurn)
        {
            return LifecycleResolutionResult.NotMatched(providerId, directive);
        }

        var matches = _lifecycle.Resolve(providerId, directive);
        if (matches.Count == 0)
        {
            return LifecycleResolutionResult.NotMatched(providerId, directive);
        }

        var trackedCount = 0;
        var deliveredCount = 0;
        var resolvedUpdates = new List<AgentEvent>();
        foreach (var match in matches)
        {
            if (match.PreviousState == EventLifecycleState.Tracked)
            {
                trackedCount++;
                continue;
            }

            deliveredCount++;
            if (match.DeliverableEvent is null)
            {
                continue;
            }

            var index = _recentEvents.FindIndex(item => item.EventId == match.DeliverableEvent.EventId);
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
            _lifecycle.UpdateDeliverable(providerId, resolved.EventId, resolved);
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

        return new LifecycleResolutionResult(
            Matched: true,
            TrackedResolved: trackedCount,
            DeliveredResolved: deliveredCount,
            persistenceSucceeded,
            providerId,
            directive.SessionIdHash,
            directive.TurnIdHash,
            directive.ToolUseIdHash,
            directive.ToolCategory);
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

    private async Task TryPublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _eventPublisher.PublishAsync(agentEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Delivery is best-effort and cannot change the loopback acceptance contract.
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
}

/// <summary>Contains only sanitized event-pipeline result metadata.</summary>
public sealed record EventPipelineResult(
    AgentEvent? Event,
    NormalizedAgentEvent? NormalizedEvent,
    bool IsDuplicate,
    bool PersistenceSucceeded,
    int EventCount,
    EventLifecycleState? LifecycleState,
    LifecycleResolutionResult LifecycleResolution);

/// <summary>Contains content-free provider-scoped lifecycle resolution metadata.</summary>
public sealed record LifecycleResolutionResult(
    bool Matched,
    int TrackedResolved,
    int DeliveredResolved,
    bool PersistenceSucceeded,
    ProviderId ProviderId,
    string? SessionIdHash,
    string? TurnIdHash,
    string? ToolUseIdHash,
    string? ToolCategory)
{
    internal static LifecycleResolutionResult NotMatched(
        ProviderId providerId,
        LifecycleDirective directive) => new(
            false,
            0,
            0,
            true,
            providerId,
            directive.SessionIdHash,
            directive.TurnIdHash,
            directive.ToolUseIdHash,
            directive.ToolCategory);
}
