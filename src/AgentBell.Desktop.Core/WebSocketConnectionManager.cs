using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Defines bounded, testable WebSocket resource limits.</summary>
public sealed record WebSocketServerOptions
{
    /// <summary>Gets the maximum authenticated clients.</summary>
    public int MaximumClients { get; init; } = 16;

    /// <summary>Gets the bounded per-client outbound capacity.</summary>
    public int QueueCapacity { get; init; } = 128;

    /// <summary>Gets the maximum client message size.</summary>
    public int MaximumMessageBytes { get; init; } = 16 * 1024;

    /// <summary>Gets the application-level ping interval.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the maximum interval without a valid pong.</summary>
    public TimeSpan ClientTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the short window reserved for an initial ordered resume request.</summary>
    public TimeSpan InitialResumeGracePeriod { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Queues sanitized events to all authenticated WebSocket clients.</summary>
public sealed class WebSocketEventPublisher : IEventPublisher
{
    private readonly WebSocketConnectionManager _connectionManager;

    /// <summary>Initializes the publisher for the shared connection manager.</summary>
    public WebSocketEventPublisher(WebSocketConnectionManager connectionManager)
    {
        _connectionManager = connectionManager
            ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        _connectionManager.Publish(agentEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Manages isolated bounded queues for authenticated WebSocket clients.</summary>
public sealed class WebSocketConnectionManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };

    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly IDesktopDiagnosticLogger _diagnosticLogger;
    private readonly TimeProvider _timeProvider;
    private readonly WebSocketServerOptions _options;
    private readonly SemaphoreSlim _clientSlots;

    /// <summary>Initializes a connection manager with bounded defaults.</summary>
    public WebSocketConnectionManager(
        IDesktopDiagnosticLogger diagnosticLogger,
        TimeProvider? timeProvider = null,
        WebSocketServerOptions? options = null)
    {
        _diagnosticLogger = diagnosticLogger
            ?? throw new ArgumentNullException(nameof(diagnosticLogger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new WebSocketServerOptions();
        ValidateOptions(_options);
        _clientSlots = new SemaphoreSlim(_options.MaximumClients, _options.MaximumClients);
    }

    /// <summary>Gets the current authenticated connection count.</summary>
    public int ActiveConnectionCount => _connections.Count;

    /// <summary>Runs one authenticated client until close, timeout, or cancellation.</summary>
    public async Task RunClientAsync(
        WebSocket webSocket,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(eventPipeline);

        if (!_clientSlots.Wait(0))
        {
            await SendErrorAndCloseAsync(
                webSocket,
                "server_busy",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var connectionId = Guid.NewGuid().ToString("N")[..8];
        ClientConnection connection;
        try
        {
            connection = new ClientConnection(
                connectionId,
                webSocket,
                pairing,
                eventPipeline,
                _diagnosticLogger,
                _timeProvider,
                _options);
        }
        catch
        {
            _clientSlots.Release();
            throw;
        }
        if (!_connections.TryAdd(connectionId, connection))
        {
            _clientSlots.Release();
            await SendErrorAndCloseAsync(
                webSocket,
                "server_busy",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Record(
            connectionId,
            "connected",
            authenticated: true,
            activeConnections: _connections.Count);
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _clientSlots.Release();
            await connection.DisposeAsync().ConfigureAwait(false);
            Record(
                connectionId,
                "disconnected",
                authenticated: true,
                activeConnections: _connections.Count);
        }
    }

    /// <summary>Queues an event without waiting for any network client.</summary>
    public void Publish(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        foreach (var connection in _connections.Values)
        {
            if (!connection.TryEnqueueEvent(agentEvent))
            {
                connection.AbortSlowClient();
                Record(
                    connection.ConnectionId,
                    "slow_client",
                    authenticated: true,
                    sequence: agentEvent.Sequence,
                    queueDepth: connection.QueueDepth,
                    activeConnections: _connections.Count);
            }
        }

        Record(
            connectionId: null,
            result: "broadcast_queued",
            authenticated: null,
            sequence: agentEvent.Sequence,
            activeConnections: _connections.Count);
    }

    /// <summary>Stops every client without letting one socket delay Desktop shutdown.</summary>
    public async Task CloseAllAsync(CancellationToken cancellationToken)
    {
        var connections = _connections.Values.ToArray();
        foreach (var connection in connections)
        {
            connection.RequestShutdown();
        }

        try
        {
            await Task.WhenAll(connections.Select(item => item.Completion))
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            foreach (var connection in connections)
            {
                connection.AbortSlowClient();
            }
        }
    }

    private static void ValidateOptions(WebSocketServerOptions options)
    {
        if (options.MaximumClients < 5
            || options.QueueCapacity <= 0
            || options.MaximumMessageBytes <= 0
            || options.PingInterval <= TimeSpan.Zero
            || options.ClientTimeout <= options.PingInterval
            || options.InitialResumeGracePeriod <= TimeSpan.Zero
            || options.InitialResumeGracePeriod >= options.ClientTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static async Task SendErrorAndCloseAsync(
        WebSocket webSocket,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ErrorMessage { Code = code },
                SerializerOptions);
            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
            await webSocket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                code,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            webSocket.Abort();
        }
    }

    private void Record(
        string? connectionId,
        string result,
        bool? authenticated,
        long? sequence = null,
        int? queueDepth = null,
        int? activeConnections = null)
    {
        try
        {
            _diagnosticLogger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = _timeProvider.GetUtcNow(),
                EventType = "websocket",
                HttpStatusCode = 0,
                ElapsedMilliseconds = 0,
                PersistenceSucceeded = true,
                ConnectionId = connectionId,
                Authenticated = authenticated,
                Sequence = sequence,
                QueueDepth = queueDepth,
                Result = result,
                ActiveConnections = activeConnections,
            });
        }
        catch
        {
            // Diagnostics cannot affect transport state.
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly WebSocket _webSocket;
        private readonly PairingConfigurationSession _pairing;
        private readonly EventPipeline _eventPipeline;
        private readonly IDesktopDiagnosticLogger _diagnosticLogger;
        private readonly TimeProvider _timeProvider;
        private readonly WebSocketServerOptions _options;
        private readonly Channel<object> _outbound;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _eventGate = new();
        private readonly SortedDictionary<long, AgentEvent> _pendingEvents = [];
        private readonly LruEventIdSet _sentEventIds = new(EventPipeline.DeduplicationCapacity);

        private Task _completion = Task.CompletedTask;
        private long _lastPingTimestamp;
        private long _lastValidPongTimestamp;
        private long _lastQueuedSequence;
        private int _queueDepth;
        private int _serverCloseInitiated;
        private bool _awaitingInitialResume = true;

        public ClientConnection(
            string connectionId,
            WebSocket webSocket,
            PairingConfigurationSession pairing,
            EventPipeline eventPipeline,
            IDesktopDiagnosticLogger diagnosticLogger,
            TimeProvider timeProvider,
            WebSocketServerOptions options)
        {
            ConnectionId = connectionId;
            _webSocket = webSocket;
            _pairing = pairing;
            _eventPipeline = eventPipeline;
            _diagnosticLogger = diagnosticLogger;
            _timeProvider = timeProvider;
            _options = options;
            _lastValidPongTimestamp = timeProvider.GetTimestamp();
            _outbound = Channel.CreateBounded<object>(new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        public string ConnectionId { get; }

        public int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));

        public Task Completion => _completion;

        public bool TryEnqueue(object message)
        {
            if (!_outbound.Writer.TryWrite(message))
            {
                return false;
            }

            Interlocked.Increment(ref _queueDepth);
            return true;
        }

        public bool TryEnqueueEvent(AgentEvent agentEvent)
        {
            lock (_eventGate)
            {
                if (_awaitingInitialResume)
                {
                    if (_pendingEvents.Count >= _options.QueueCapacity)
                    {
                        return false;
                    }

                    if (agentEvent.ResolvedAt is not null)
                    {
                        var superseded = _pendingEvents
                            .Where(item => item.Value.EventId == agentEvent.EventId)
                            .Select(item => item.Key)
                            .ToArray();
                        foreach (var sequence in superseded)
                        {
                            _pendingEvents.Remove(sequence);
                        }
                    }

                    _pendingEvents[agentEvent.Sequence] = agentEvent;
                    return true;
                }

                return TryQueueEventLocked(agentEvent);
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _completion = RunCoreAsync(cancellationToken);
            await _completion.ConfigureAwait(false);
        }

        public void AbortSlowClient()
        {
            try
            {
                _shutdown.Cancel();
                _webSocket.Abort();
            }
            catch (ObjectDisposedException)
            {
                // A concurrently completed client is already isolated.
            }
        }

        public void RequestShutdown()
        {
            try
            {
                _outbound.Writer.TryComplete();
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrently completed client needs no additional shutdown.
            }
        }

        public ValueTask DisposeAsync()
        {
            _shutdown.Dispose();
            _webSocket.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
            var token = linked.Token;
            var history = await _eventPipeline.GetHistoryAsync(
                long.MaxValue,
                token).ConfigureAwait(false);
            if (!TryEnqueue(new HelloMessage
            {
                DeviceName = _pairing.Configuration.DeviceName ?? "Windows PC",
                DeviceId = _pairing.Configuration.DeviceId ?? string.Empty,
                LatestSequence = history.LatestSequence,
                ServerTime = _timeProvider.GetUtcNow(),
            }))
            {
                return;
            }

            var sendTask = SendLoopAsync(token);
            var receiveTask = ReceiveLoopAsync(token);
            var heartbeatTask = HeartbeatLoopAsync(token);
            var resumeGraceTask = CompleteInitialResumeGraceAsync(token);
            await Task.WhenAny(sendTask, receiveTask, heartbeatTask).ConfigureAwait(false);
            if (Volatile.Read(ref _serverCloseInitiated) != 0)
            {
                try
                {
                    await Task.WhenAny(
                        receiveTask,
                        Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The normal shutdown path below contains peer failures.
                }
            }

            linked.Cancel();
            _outbound.Writer.TryComplete();
            await SuppressAsync(sendTask).ConfigureAwait(false);
            await SuppressAsync(receiveTask).ConfigureAwait(false);
            await SuppressAsync(heartbeatTask).ConfigureAwait(false);
            await SuppressAsync(resumeGraceTask).ConfigureAwait(false);

            try
            {
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "closed",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                _webSocket.Abort();
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Decrement(ref _queueDepth);
                if (message is CloseCommand closeCommand)
                {
                    try
                    {
                        await _webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.PolicyViolation,
                            closeCommand.Description,
                            cancellationToken).ConfigureAwait(false);
                        closeCommand.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        closeCommand.Completion.TrySetException(exception);
                    }

                    return;
                }

                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    message,
                    message.GetType(),
                    SerializerOptions);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (message.Length + result.Count > _options.MaximumMessageBytes)
                    {
                        TryEnqueue(new ErrorMessage { Code = "invalid_message" });
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    TryEnqueue(new ErrorMessage { Code = "unsupported_type" });
                    continue;
                }

                string json;
                try
                {
                    json = StrictUtf8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                }
                catch (DecoderFallbackException)
                {
                    TryEnqueue(new ErrorMessage { Code = "invalid_json" });
                    continue;
                }

                await HandleClientMessageAsync(json, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task HandleClientMessageAsync(
            string json,
            CancellationToken cancellationToken)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            }
            catch (JsonException)
            {
                TryEnqueue(new ErrorMessage { Code = "invalid_json" });
                return;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    TryEnqueue(new ErrorMessage { Code = "invalid_message" });
                    return;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "resume", StringComparison.Ordinal))
                {
                    await HandleResumeAsync(document.RootElement, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(type, "pong", StringComparison.Ordinal))
                {
                    HandlePong(document.RootElement);
                    return;
                }

                TryEnqueue(new ErrorMessage { Code = "unsupported_type" });
            }
        }

        private async Task HandleResumeAsync(
            JsonElement root,
            CancellationToken cancellationToken)
        {
            if (!root.TryGetProperty("lastSequence", out var sequenceElement)
                || sequenceElement.ValueKind != JsonValueKind.Number
                || !sequenceElement.TryGetInt64(out var lastSequence)
                || lastSequence < 0)
            {
                TryEnqueue(new ErrorMessage { Code = "invalid_sequence" });
                return;
            }

            var history = await _eventPipeline.GetHistoryAsync(
                lastSequence,
                cancellationToken).ConfigureAwait(false);
            var replayCount = 0;
            var queued = true;
            lock (_eventGate)
            {
                var combined = history.Events
                    .Concat(_pendingEvents.Values)
                    .GroupBy(item => item.EventId, StringComparer.Ordinal)
                    .Select(group => group.MaxBy(item => item.Sequence)!)
                    .OrderBy(item => item.Sequence)
                    .ToArray();

                _pendingEvents.Clear();
                _awaitingInitialResume = false;
                foreach (var agentEvent in combined)
                {
                    if (agentEvent.Sequence <= lastSequence
                        || agentEvent.Sequence <= _lastQueuedSequence)
                    {
                        continue;
                    }

                    if (!TryQueueEventLocked(agentEvent))
                    {
                        queued = false;
                        break;
                    }

                    replayCount++;
                }
            }

            if (!queued)
            {
                AbortSlowClient();
                return;
            }

            RecordMessage("resume", lastSequence, replayCount, "success");
        }

        private async Task CompleteInitialResumeGraceAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(
                _options.InitialResumeGracePeriod,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            var queued = true;
            lock (_eventGate)
            {
                if (!_awaitingInitialResume)
                {
                    return;
                }

                _awaitingInitialResume = false;
                foreach (var agentEvent in _pendingEvents.Values)
                {
                    if (!TryQueueEventLocked(agentEvent))
                    {
                        queued = false;
                        break;
                    }
                }

                _pendingEvents.Clear();
            }

            if (!queued)
            {
                AbortSlowClient();
            }
        }

        private bool TryQueueEventLocked(AgentEvent agentEvent)
        {
            if (agentEvent.ResolvedAt is null && !_sentEventIds.TryAdd(agentEvent.EventId))
            {
                return true;
            }

            else if (agentEvent.ResolvedAt is not null)
            {
                _sentEventIds.TryAdd(agentEvent.EventId);
            }

            if (!TryEnqueue(new EventMessage { Payload = agentEvent }))
            {
                return false;
            }

            _lastQueuedSequence = Math.Max(_lastQueuedSequence, agentEvent.Sequence);
            return true;
        }

        private void HandlePong(JsonElement root)
        {
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.Number
                || !timestampElement.TryGetInt64(out var timestamp)
                || Interlocked.CompareExchange(
                    ref _lastPingTimestamp,
                    value: 0,
                    comparand: timestamp) != timestamp)
            {
                TryEnqueue(new ErrorMessage { Code = "invalid_message" });
                return;
            }

            Interlocked.Exchange(
                ref _lastValidPongTimestamp,
                _timeProvider.GetTimestamp());
            RecordMessage("pong", null, null, "success");
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                    _options.PingInterval,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
                var monotonicNow = _timeProvider.GetTimestamp();
                var lastPong = Interlocked.Read(ref _lastValidPongTimestamp);
                if (_timeProvider.GetElapsedTime(lastPong, monotonicNow) >= _options.ClientTimeout)
                {
                    RecordMessage("ping", null, null, "pong_timeout");
                    Volatile.Write(ref _serverCloseInitiated, 1);
                    var closeCommand = new CloseCommand("pong_timeout");
                    if (!TryEnqueue(closeCommand))
                    {
                        AbortSlowClient();
                        return;
                    }

                    try
                    {
                        await closeCommand.Completion.Task
                            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        AbortSlowClient();
                    }

                    return;
                }

                if (Interlocked.Read(ref _lastPingTimestamp) != 0)
                {
                    continue;
                }

                var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (Interlocked.CompareExchange(
                        ref _lastPingTimestamp,
                        value: now,
                        comparand: 0) != 0)
                {
                    continue;
                }

                if (!TryEnqueue(new PingMessage { Timestamp = now }))
                {
                    Interlocked.CompareExchange(
                        ref _lastPingTimestamp,
                        value: 0,
                        comparand: now);
                    AbortSlowClient();
                    return;
                }
            }
        }

        private void RecordMessage(
            string messageType,
            long? sequence,
            int? replayCount,
            string result)
        {
            try
            {
                _diagnosticLogger.Record(new DesktopDiagnosticEvent
                {
                    Timestamp = _timeProvider.GetUtcNow(),
                    EventType = "websocket",
                    HttpStatusCode = 0,
                    ElapsedMilliseconds = 0,
                    PersistenceSucceeded = true,
                    ConnectionId = ConnectionId,
                    Authenticated = true,
                    MessageType = messageType,
                    Sequence = sequence,
                    QueueDepth = QueueDepth,
                    Result = result,
                    ReplayCount = replayCount,
                });
            }
            catch
            {
                // Diagnostics cannot affect the client protocol.
            }
        }

        private static async Task SuppressAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The owning connection contains individual socket failures.
            }
        }

        private sealed record CloseCommand(string Description)
        {
            public TaskCompletionSource Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
