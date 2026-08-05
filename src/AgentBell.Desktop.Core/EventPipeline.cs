using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Serializes event acceptance, deduplication, sequencing, and local persistence.</summary>
public sealed class EventPipeline
{
    /// <summary>The maximum number of event identifiers retained for process-lifetime deduplication.</summary>
    public const int DeduplicationCapacity = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEventStore _eventStore;
    private readonly CodexEventTransformer _transformer;
    private readonly IEventPublisher _eventPublisher;
    private readonly LruEventIdSet _deduplication = new(DeduplicationCapacity);
    private readonly List<AgentEvent> _recentEvents = [];

    private long _sequence;
    private bool _initialized;

    /// <summary>Initializes the pipeline with its store and source transformer.</summary>
    public EventPipeline(
        IEventStore eventStore,
        CodexEventTransformer transformer,
        IEventPublisher? eventPublisher = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
        _eventPublisher = eventPublisher ?? NoOpEventPublisher.Instance;
    }

    /// <summary>Restores deduplication and sequence state from sanitized recent events.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    /// <summary>Accepts one validated Stop event and returns a content-free processing result.</summary>
    public async Task<EventAcceptanceResult> AcceptAsync(
        CodexStopHookPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("The event pipeline is not initialized.");
            }

            var candidate = _transformer.Transform(payload);
            if (!_deduplication.TryAdd(candidate.EventId))
            {
                return new EventAcceptanceResult(
                    candidate,
                    IsDuplicate: true,
                    PersistenceSucceeded: true,
                    _recentEvents.Count);
            }

            var accepted = candidate with { Sequence = checked(++_sequence) };
            _recentEvents.Add(accepted);
            if (_recentEvents.Count > JsonEventStore.MaxRecentEvents)
            {
                _recentEvents.RemoveAt(0);
            }

            var persistenceSucceeded = await _eventStore.SaveAsync(
                _recentEvents.ToArray(),
                cancellationToken).ConfigureAwait(false);

            try
            {
                await _eventPublisher.PublishAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // LAN delivery is best-effort and cannot change the M1 HTTP contract.
            }

            return new EventAcceptanceResult(
                accepted,
                IsDuplicate: false,
                persistenceSucceeded,
                _recentEvents.Count);
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
            if (!_initialized)
            {
                throw new InvalidOperationException("The event pipeline is not initialized.");
            }

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
}

/// <summary>Contains only sanitized event-acceptance metadata.</summary>
/// <param name="Event">The sanitized accepted or duplicate event candidate.</param>
/// <param name="IsDuplicate">Whether the event identifier was already retained.</param>
/// <param name="PersistenceSucceeded">Whether the current history snapshot was persisted.</param>
/// <param name="EventCount">The number of recent in-memory events.</param>
public sealed record EventAcceptanceResult(
    AgentEvent Event,
    bool IsDuplicate,
    bool PersistenceSucceeded,
    int EventCount);
