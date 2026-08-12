using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentBell.Desktop.Tests;

public sealed class LanHostIntegrationTests
{
    [Fact]
    public async Task HttpEndpoints_EnforceAuthenticationAndDoNotExposeCredential()
    {
        await using var server = await LanTestServer.StartAsync();
        using var client = new HttpClient { BaseAddress = server.HttpBaseAddress };

        using var health = await client.GetAsync(LanHost.HealthPath);
        Assert.Equal(200, (int)health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());

        using var pair = await client.GetAsync(LanHost.PairingPagePath);
        var pairHtml = await pair.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)pair.StatusCode);
        Assert.Contains("no-store", pair.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(server.Pairing.Token.Value, pairHtml, StringComparison.Ordinal);

        using var missing = await client.GetAsync(LanHost.StatusPath);
        Assert.Equal(401, (int)missing.StatusCode);

        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, LanHost.StatusPath);
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        using var wrong = await client.SendAsync(wrongRequest);
        Assert.Equal(403, (int)wrong.StatusCode);

        using var queryOnly = await client.GetAsync(
            $"{LanHost.StatusPath}?access_token={server.Pairing.Token.Value}");
        Assert.Equal(401, (int)queryOnly.StatusCode);

        using var validRequest = new HttpRequestMessage(HttpMethod.Get, LanHost.StatusPath);
        validRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            server.Pairing.Token.Value);
        using var valid = await client.SendAsync(validRequest);
        var statusJson = await valid.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)valid.StatusCode);
        using var status = JsonDocument.Parse(statusJson);
        Assert.Equal(AgentBellProtocol.ProtocolVersion,
            status.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(AgentBellProtocol.ServerVersion,
            status.RootElement.GetProperty("serverVersion").GetString());
        Assert.Equal(server.Port, status.RootElement.GetProperty("lanPort").GetInt32());
        Assert.DoesNotContain(server.Pairing.Token.Value, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedPairingToken", statusJson, StringComparison.Ordinal);

        using var hookOnLan = await client.PostAsync(
            DesktopHttpContract.EventsPath,
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(404, (int)hookOnLan.StatusCode);

        var diagnostics = JsonSerializer.Serialize(server.Logger.Events);
        Assert.DoesNotContain(server.Pairing.Token.Value, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_BearerAndQueryAuthenticationBothReceiveHello()
    {
        await using var server = await LanTestServer.StartAsync();

        using var bearer = new ClientWebSocket();
        bearer.Options.SetRequestHeader("Authorization", $"Bearer {server.Pairing.Token.Value}");
        await bearer.ConnectAsync(server.WebSocketAddress, CancellationToken.None);
        using var bearerHello = JsonDocument.Parse(await ReceiveTextAsync(bearer));
        Assert.Equal("hello", bearerHello.RootElement.GetProperty("type").GetString());
        await bearer.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);

        using var query = new ClientWebSocket();
        var queryAddress = new Uri(
            $"{server.WebSocketAddress}?access_token={server.Pairing.Token.Value}");
        await query.ConnectAsync(queryAddress, CancellationToken.None);
        using var queryHello = JsonDocument.Parse(await ReceiveTextAsync(query));
        Assert.Equal("hello", queryHello.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(
            server.Pairing.Token.Value,
            JsonSerializer.Serialize(server.Logger.Events),
            StringComparison.Ordinal);
        await query.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task WebSocket_MissingOrWrongTokenRejectsUpgrade(string? token)
    {
        await using var server = await LanTestServer.StartAsync();
        using var socket = new ClientWebSocket();
        var address = token is null
            ? server.WebSocketAddress
            : new Uri($"{server.WebSocketAddress}?access_token={token}");

        await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(address, CancellationToken.None));
    }

    [Fact]
    public async Task WebSocket_ResumeReplaysAscendingThenBroadcastsNewEventOnce()
    {
        await using var server = await LanTestServer.StartAsync();
        await server.AcceptAsync("turn-1");
        await server.AcceptAsync("turn-2");

        using var socket = await server.ConnectWithQueryAsync();
        using var hello = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal(2, hello.RootElement.GetProperty("latestSequence").GetInt64());
        await SendTextAsync(socket, "{\"type\":\"resume\",\"lastSequence\":0}");

        using var first = JsonDocument.Parse(await ReceiveTextAsync(socket));
        using var second = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal(1, first.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
        Assert.Equal(2, second.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());

        await server.AcceptAsync("turn-3");
        using var third = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal(3, third.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
        await server.AcceptAsync("turn-3");

        using var noDuplicate = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReceiveTextAsync(socket, noDuplicate.Token));
    }

    [Fact]
    public async Task WebSocket_ReconnectResumeRecoversOfflineEvent()
    {
        await using var server = await LanTestServer.StartAsync();
        await server.AcceptAsync("turn-online");
        using (var firstSocket = await server.ConnectWithQueryAsync())
        {
            _ = await ReceiveTextAsync(firstSocket);
            await SendTextAsync(firstSocket, "{\"type\":\"resume\",\"lastSequence\":0}");
            using var online = JsonDocument.Parse(await ReceiveTextAsync(firstSocket));
            Assert.Equal(1, online.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
            await firstSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "offline",
                CancellationToken.None);
        }

        await server.AcceptAsync("turn-offline");
        using var secondSocket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(secondSocket);
        await SendTextAsync(secondSocket, "{\"type\":\"resume\",\"lastSequence\":1}");
        using var replay = JsonDocument.Parse(await ReceiveTextAsync(secondSocket));
        Assert.Equal(2, replay.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task WebSocket_ActionRequired_IsBroadcastOnceAndReplayedWithAdditiveFields()
    {
        await using var server = await LanTestServer.StartAsync();
        using var socket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(socket);
        await SendTextAsync(socket, "{\"type\":\"resume\",\"lastSequence\":0}");

        await server.AcceptActionAsync();
        await server.AcceptActionAsync();

        using var live = JsonDocument.Parse(await ReceiveTextAsync(socket));
        var payload = live.RootElement.GetProperty("payload");
        Assert.Equal(AgentEventCategories.ActionRequired, payload.GetProperty("category").GetString());
        Assert.Equal(AgentActionTypes.PermissionRequired, payload.GetProperty("actionType").GetString());
        Assert.Equal(AgentToolCategories.Command, payload.GetProperty("toolCategory").GetString());
        Assert.False(payload.TryGetProperty("toolInput", out _));
        Assert.False(payload.TryGetProperty("command", out _));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None);
        using var replaySocket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(replaySocket);
        await SendTextAsync(replaySocket, "{\"type\":\"resume\",\"lastSequence\":0}");
        using var replay = JsonDocument.Parse(await ReceiveTextAsync(replaySocket));
        Assert.Equal(
            AgentActionTypes.PermissionRequired,
            replay.RootElement.GetProperty("payload").GetProperty("actionType").GetString());
        using var noDuplicate = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReceiveTextAsync(replaySocket, noDuplicate.Token));
    }

    [Fact]
    public async Task WebSocket_RepeatedResumeDoesNotReplaySameEventIdTwice()
    {
        await using var server = await LanTestServer.StartAsync();
        await server.AcceptAsync("turn-once");
        using var socket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(socket);
        await SendTextAsync(socket, "{\"type\":\"resume\",\"lastSequence\":0}");
        _ = await ReceiveTextAsync(socket);

        await SendTextAsync(socket, "{\"type\":\"resume\",\"lastSequence\":0}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReceiveTextAsync(socket, timeout.Token));
    }

    [Fact]
    public async Task WebSocket_EventAcceptedBeforeInitialResumeRemainsInSequenceOrder()
    {
        await using var server = await LanTestServer.StartAsync();
        await server.AcceptAsync("turn-before-connect");
        using var socket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(socket);
        await server.AcceptAsync("turn-before-resume");

        await SendTextAsync(socket, "{\"type\":\"resume\",\"lastSequence\":0}");
        using var first = JsonDocument.Parse(await ReceiveTextAsync(socket));
        using var second = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal(1, first.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
        Assert.Equal(2, second.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData("{not-json", "invalid_json")]
    [InlineData("{}", "invalid_message")]
    [InlineData("{\"type\":\"unknown\"}", "unsupported_type")]
    [InlineData("{\"type\":\"resume\",\"lastSequence\":-1}", "invalid_sequence")]
    public async Task WebSocket_InvalidClientMessagesReturnStableErrors(
        string message,
        string expectedCode)
    {
        await using var server = await LanTestServer.StartAsync();
        using var socket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(socket);

        await SendTextAsync(socket, message);
        using var error = JsonDocument.Parse(await ReceiveTextAsync(socket));

        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal(expectedCode, error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WebSocket_FiveConcurrentClientsReceiveSameNewEvent()
    {
        await using var server = await LanTestServer.StartAsync();
        var clients = new List<ClientWebSocket>();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var client = await server.ConnectWithQueryAsync();
                clients.Add(client);
                _ = await ReceiveTextAsync(client);
                await SendTextAsync(client, "{\"type\":\"resume\",\"lastSequence\":0}");
            }

            await server.AcceptAsync("turn-multicast");
            var received = await Task.WhenAll(
                clients.Select(client => ReceiveTextAsync(client)));
            Assert.All(received, message =>
            {
                using var document = JsonDocument.Parse(message);
                Assert.Equal("event", document.RootElement.GetProperty("type").GetString());
                Assert.Equal(1,
                    document.RootElement.GetProperty("payload").GetProperty("sequence").GetInt64());
            });
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task WebSocket_PingPongKeepsConnectionThenMissingPongTimesOut()
    {
        var options = new WebSocketServerOptions
        {
            MaximumClients = 5,
            QueueCapacity = 16,
            PingInterval = TimeSpan.FromSeconds(5),
            ClientTimeout = TimeSpan.FromSeconds(15),
            InitialResumeGracePeriod = TimeSpan.FromSeconds(1),
        };
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await LanTestServer.StartAsync(
            options,
            watchdog.Token,
            clock);
        ClientWebSocket? socket = null;
        var stage = "host_ready";
        var pingCount = 0;
        var pongSentCount = 0;
        var pongConfirmedCount = 0;
        DateTimeOffset? heartbeatDeadline = null;
        string? serverCloseReason = null;
        var closeFrameObserved = false;
        try
        {
            Assert.True(server.IsRunning);

            stage = "websocket_connect";
            using (var connectLimit = CreatePhaseTimeout(watchdog.Token))
            {
                socket = await server.ConnectWithQueryAsync(connectLimit.Token);
                Assert.Equal(WebSocketState.Open, socket.State);

                stage = "hello_received";
                using var hello = JsonDocument.Parse(
                    await ReceiveTextAsync(socket, connectLimit.Token));
                Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            }

            stage = "phase_a_first_ping";
            long firstPingTimestamp;
            using (var firstPingLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await clock.WaitForPendingTimerAsync(firstPingLimit.Token);
                clock.Advance(options.PingInterval);
                using var firstPing = JsonDocument.Parse(
                    await ReceiveTextAsync(socket, firstPingLimit.Token));
                Assert.Equal("ping", firstPing.RootElement.GetProperty("type").GetString());
                firstPingTimestamp = firstPing.RootElement.GetProperty("timestamp").GetInt64();
                pingCount++;
            }

            stage = "phase_a_outstanding_ping_guard";
            using (var outstandingPingLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await clock.WaitForPendingTimerAsync(outstandingPingLimit.Token);
                clock.Advance(options.PingInterval);
            }

            stage = "phase_a_pong_sent";
            using (var pongSendLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await SendTextAsync(
                    socket,
                    $"{{\"type\":\"pong\",\"timestamp\":{firstPingTimestamp}}}",
                    pongSendLimit.Token);
                pongSentCount++;
            }

            stage = "phase_a_pong_confirmed";
            DesktopDiagnosticEvent pongConfirmation;
            using (var pongConfirmationLimit = CreatePhaseTimeout(watchdog.Token))
            {
                pongConfirmation = await server.Logger.WaitForAsync(
                    item => string.Equals(item.MessageType, "pong", StringComparison.Ordinal)
                        && string.Equals(item.Result, "success", StringComparison.Ordinal),
                    pongConfirmationLimit.Token);
            }
            pongConfirmedCount++;
            heartbeatDeadline = pongConfirmation.Timestamp + options.ClientTimeout;
            Assert.Equal(WebSocketState.Open, socket.State);
            Assert.True(server.IsRunning);

            stage = "phase_b_second_ping";
            using (var secondPingLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await clock.WaitForPendingTimerAsync(secondPingLimit.Token);
                clock.Advance(options.PingInterval);
                using var secondPing = JsonDocument.Parse(
                    await ReceiveTextAsync(socket, secondPingLimit.Token));
                Assert.Equal("ping", secondPing.RootElement.GetProperty("type").GetString());
                pingCount++;
            }

            stage = "phase_b_missing_pong";
            using var closeReceiveLimit = CreatePhaseTimeout(watchdog.Token);
            var closeObservationTask = ReceiveCloseAsync(
                socket,
                messageType =>
                {
                    if (string.Equals(messageType, "ping", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref pingCount);
                    }
                },
                closeReceiveLimit.Token);

            using (var heartbeatDeadlineLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await clock.WaitForPendingTimerAsync(heartbeatDeadlineLimit.Token);
                clock.Advance(options.ClientTimeout - options.PingInterval);
            }

            DesktopDiagnosticEvent timeoutDiagnostic;
            using (var timeoutSignalLimit = CreatePhaseTimeout(watchdog.Token))
            {
                timeoutDiagnostic = await server.Logger.WaitForAsync(
                    item => string.Equals(item.MessageType, "ping", StringComparison.Ordinal)
                        && string.Equals(item.Result, "pong_timeout", StringComparison.Ordinal),
                    timeoutSignalLimit.Token);
            }
            serverCloseReason = timeoutDiagnostic.Result;
            Assert.True(timeoutDiagnostic.Timestamp >= pongConfirmation.Timestamp);

            stage = "phase_b_server_close";
            var closeObservation = await closeObservationTask;
            closeFrameObserved = true;
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, closeObservation.Status);
            Assert.Equal("pong_timeout", closeObservation.Description);

            stage = "phase_b_close_ack";
            using (var closeAckLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "ack",
                    closeAckLimit.Token);
            }

            stage = "phase_b_disconnected";
            using (var disconnectLimit = CreatePhaseTimeout(watchdog.Token))
            {
                await server.Logger.WaitForAsync(
                    item => string.Equals(item.Result, "disconnected", StringComparison.Ordinal),
                    disconnectLimit.Token);
            }

            Assert.Equal(0, server.Manager.ActiveConnectionCount);
            Assert.True(server.IsRunning);
            Assert.False(watchdog.IsCancellationRequested);
            Assert.Equal(2, pingCount);
            Assert.Equal(1, pongSentCount);
            Assert.Equal(1, pongConfirmedCount);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                CreateHeartbeatFailureDiagnostic(
                    stage,
                    socket,
                    pingCount,
                    pongSentCount,
                    pongConfirmedCount,
                    heartbeatDeadline,
                    serverCloseReason,
                    closeFrameObserved,
                    server,
                    watchdog.IsCancellationRequested),
                exception);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    [Fact]
    public async Task ProductionLanHost_RejectsLoopbackAndPublicAddresses()
    {
        var directory = CreateDirectory();
        try
        {
            using var pairing = await TestPairingFactory.CreateAsync(directory);
            var store = new InMemoryEventStore();
            var pipeline = new EventPipeline(store, new CodexEventTransformer());
            await pipeline.InitializeAsync(CancellationToken.None);
            var logger = new CollectingDesktopDiagnosticLogger();
            var manager = new WebSocketConnectionManager(logger);

            Assert.Throws<ArgumentException>(() => LanHost.Build(
                IPAddress.Loopback,
                17864,
                pairing,
                pipeline,
                manager,
                logger));
            Assert.Throws<ArgumentException>(() => LanHost.Build(
                IPAddress.Parse("8.8.8.8"),
                17864,
                pairing,
                pipeline,
                manager,
                logger));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static CancellationTokenSource CreatePhaseTimeout(
        CancellationToken watchdogToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(watchdogToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static async Task<WebSocketCloseObservation> ReceiveCloseAsync(
        ClientWebSocket socket,
        Action<string> textMessageObserver,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new WebSocketCloseObservation(
                        result.CloseStatus,
                        result.CloseStatusDescription);
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WebSocketException("Unexpected non-text message before server close.");
            }

            using var document = JsonDocument.Parse(message.ToArray());
            var messageType = document.RootElement.GetProperty("type").GetString()
                ?? "missing";
            textMessageObserver(messageType);
        }
    }

    private static string CreateHeartbeatFailureDiagnostic(
        string stage,
        ClientWebSocket? socket,
        int pingCount,
        int pongSentCount,
        int pongConfirmedCount,
        DateTimeOffset? heartbeatDeadline,
        string? serverCloseReason,
        bool closeFrameObserved,
        LanTestServer server,
        bool watchdogTriggered)
    {
        var serverResults = server.Logger.Events
            .Where(item => string.Equals(item.EventType, "websocket", StringComparison.Ordinal))
            .Select(item => $"{item.MessageType ?? "lifecycle"}:{item.Result ?? "none"}")
            .ToArray();
        return $"WebSocket heartbeat test failed. Phase={stage}; "
            + $"WebSocketState={socket?.State.ToString() ?? "not_created"}; "
            + $"PingReceived={pingCount}; PongSent={pongSentCount}; "
            + $"PongConfirmed={pongConfirmedCount}; "
            + $"HeartbeatDeadline={heartbeatDeadline?.ToString("O") ?? "not_set"}; "
            + $"ServerCloseReason={serverCloseReason ?? "not_recorded"}; "
            + $"CloseFrameObserved={closeFrameObserved}; HostRunning={server.IsRunning}; "
            + $"WatchdogTriggered={watchdogTriggered}; "
            + $"ServerDiagnostics={string.Join(',', serverResults)}; "
            + $"CurrentDisposeStage={server.DisposeStage}; "
            + "DisposeOrder=client_receive_complete,client_socket_dispose,"
            + "manager_close,host_stop,host_dispose,watchdog_dispose.";
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[32 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Socket closed before a text message.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-LAN-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record WebSocketCloseObservation(
        WebSocketCloseStatus? Status,
        string? Description);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly HashSet<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp;
        private TaskCompletionSource _timerAvailable = CreateSignal();

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public async Task WaitForPendingTimerAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_timers.Count > 0)
                    {
                        return;
                    }

                    signal = _timerAvailable.Task;
                }

                await signal.WaitAsync(cancellationToken);
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            ManualTimer[] dueTimers;
            lock (_gate)
            {
                _utcNow += elapsed;
                _timestamp = checked(_timestamp + elapsed.Ticks);
                dueTimers = _timers
                    .Where(timer => timer.DueTimestamp <= _timestamp)
                    .ToArray();
                foreach (var timer in dueTimers)
                {
                    if (timer.PeriodTicks == Timeout.InfiniteTimeSpan.Ticks)
                    {
                        _timers.Remove(timer);
                    }
                    else
                    {
                        timer.DueTimestamp = checked(_timestamp + timer.PeriodTicks);
                    }
                }

                ResetSignalIfEmpty();
            }

            foreach (var timer in dueTimers)
            {
                timer.Fire();
            }
        }

        private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimerDuration(dueTime, nameof(dueTime));
            ValidateTimerDuration(period, nameof(period));
            lock (_gate)
            {
                _timers.Remove(timer);
                timer.PeriodTicks = period.Ticks;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.DueTimestamp = checked(_timestamp + dueTime.Ticks);
                    _timers.Add(timer);
                    _timerAvailable.TrySetResult();
                }
                else
                {
                    ResetSignalIfEmpty();
                }

                return true;
            }
        }

        private void RemoveTimer(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
                ResetSignalIfEmpty();
            }
        }

        private void ResetSignalIfEmpty()
        {
            if (_timers.Count == 0 && _timerAvailable.Task.IsCompleted)
            {
                _timerAvailable = CreateSignal();
            }
        }

        private static void ValidateTimerDuration(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private static TaskCompletionSource CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private int _disposed;

            public long DueTimestamp { get; set; }

            public long PeriodTicks { get; set; } = Timeout.InfiniteTimeSpan.Ticks;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref _disposed) == 0
                && owner.ChangeTimer(this, dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.RemoveTimer(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }
        }
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class LanTestServer : IAsyncDisposable
    {
        private readonly string _directory;

        private LanTestServer(
            string directory,
            int port,
            PairingConfigurationSession pairing,
            EventPipeline pipeline,
            CollectingDesktopDiagnosticLogger logger,
            WebSocketConnectionManager manager,
            Microsoft.AspNetCore.Builder.WebApplication application)
        {
            _directory = directory;
            Port = port;
            Pairing = pairing;
            Pipeline = pipeline;
            Logger = logger;
            Manager = manager;
            Application = application;
        }

        public int Port { get; }

        public PairingConfigurationSession Pairing { get; }

        public EventPipeline Pipeline { get; }

        public CollectingDesktopDiagnosticLogger Logger { get; }

        public WebSocketConnectionManager Manager { get; }

        public Microsoft.AspNetCore.Builder.WebApplication Application { get; }

        public string DisposeStage { get; private set; } = "not_started";

        public bool IsRunning
        {
            get
            {
                var lifetime = Application.Services.GetRequiredService<IHostApplicationLifetime>();
                return lifetime.ApplicationStarted.IsCancellationRequested
                    && !lifetime.ApplicationStopping.IsCancellationRequested
                    && !lifetime.ApplicationStopped.IsCancellationRequested;
            }
        }

        public Uri HttpBaseAddress => new($"http://127.0.0.1:{Port}");

        public Uri WebSocketAddress => new($"ws://127.0.0.1:{Port}{AgentBellProtocol.WebSocketPath}");

        public static async Task<LanTestServer> StartAsync(
            WebSocketServerOptions? webSocketOptions = null,
            CancellationToken cancellationToken = default,
            TimeProvider? timeProvider = null)
        {
            var directory = CreateDirectory();
            PairingConfigurationSession? pairing = null;
            Microsoft.AspNetCore.Builder.WebApplication? application = null;
            try
            {
                pairing = await TestPairingFactory.CreateAsync(directory);
                var logger = new CollectingDesktopDiagnosticLogger();
                var manager = new WebSocketConnectionManager(
                    logger,
                    timeProvider,
                    options: webSocketOptions);
                var notificationSettings = new DesktopNotificationSettingsState();
                notificationSettings.Update(new DesktopNotificationSettings
                {
                    PermissionNotificationPolicy =
                        PermissionNotificationPolicy.AlwaysNotify,
                });
                var pipeline = new EventPipeline(
                    new InMemoryEventStore(),
                    new CodexEventTransformer(),
                    new WebSocketEventPublisher(manager),
                    notificationSettings: notificationSettings);
                await pipeline.InitializeAsync(CancellationToken.None);
                application = LanHost.BuildForTesting(
                    IPAddress.Loopback,
                    0,
                    pairing,
                    pipeline,
                    manager,
                    logger);
                await application.StartAsync(cancellationToken);
                var addresses = application.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()?
                    .Addresses;
                var address = Assert.Single(addresses ?? []);
                var listener = new Uri(address);
                Assert.Equal(IPAddress.Loopback.ToString(), listener.Host);
                Assert.InRange(listener.Port, 1024, 65535);
                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler) { BaseAddress = listener };
                using var health = await client.GetAsync(
                    LanHost.HealthPath,
                    cancellationToken);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                return new LanTestServer(
                    directory,
                    listener.Port,
                    pairing,
                    pipeline,
                    logger,
                    manager,
                    application);
            }
            catch
            {
                if (application is not null)
                {
                    await application.DisposeAsync();
                }

                pairing?.Dispose();
                DeleteDirectory(directory);
                throw;
            }
        }

        public Task<EventAcceptanceResult> AcceptAsync(string turnId) =>
            Pipeline.AcceptAsync(
                new CodexStopHookPayload
                {
                    HookEventName = "Stop",
                    SessionId = "lan-test-session",
                    TurnId = turnId,
                    WorkingDirectory = "C:\\Private\\AgentBell",
                    LastAssistantMessage = "完成 M2 🔔",
                },
                CancellationToken.None);

        public Task<EventAcceptanceResult> AcceptActionAsync() =>
            Pipeline.AcceptAsync(
                new SanitizedActionRequiredEvent
                {
                    EventId = "codex-action:00112233445566778899aabb",
                    SessionIdHash = "001122334455",
                    TurnIdHash = "66778899aabb",
                    ToolUseIdHash = "ccddee112233",
                    Project = "AgentBell",
                    ToolCategory = AgentToolCategories.Command,
                    OccurredAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
                },
                CancellationToken.None);

        public async Task<ClientWebSocket> ConnectWithQueryAsync(
            CancellationToken cancellationToken = default)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(
                new Uri($"{WebSocketAddress}?access_token={Pairing.Token.Value}"),
                cancellationToken);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeStage = "manager_close";
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Manager.CloseAllAsync(shutdown.Token);
            try
            {
                DisposeStage = "host_stop";
                await Application.StopAsync(shutdown.Token);
            }
            finally
            {
                DisposeStage = "host_dispose";
                await Application.DisposeAsync();
                Pairing.Dispose();
                DeleteDirectory(_directory);
                DisposeStage = "completed";
            }
        }
    }
}
