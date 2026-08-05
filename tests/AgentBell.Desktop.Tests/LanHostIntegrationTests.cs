using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;

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
        await using var server = await LanTestServer.StartAsync(new WebSocketServerOptions
        {
            MaximumClients = 5,
            QueueCapacity = 16,
            PingInterval = TimeSpan.FromMilliseconds(50),
            ClientTimeout = TimeSpan.FromMilliseconds(160),
            InitialResumeGracePeriod = TimeSpan.FromMilliseconds(10),
        });
        using var socket = await server.ConnectWithQueryAsync();
        _ = await ReceiveTextAsync(socket);

        using var ping = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
        var timestamp = ping.RootElement.GetProperty("timestamp").GetInt64();
        await SendTextAsync(
            socket,
            $"{{\"type\":\"pong\",\"timestamp\":{timestamp}}}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sawAnotherPing = false;
        while (true)
        {
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "ack",
                    CancellationToken.None);
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var document = JsonDocument.Parse(text);
            sawAnotherPing |= document.RootElement.GetProperty("type").GetString() == "ping";
        }

        Assert.True(sawAnotherPing);
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

        public Uri HttpBaseAddress => new($"http://127.0.0.1:{Port}");

        public Uri WebSocketAddress => new($"ws://127.0.0.1:{Port}{AgentBellProtocol.WebSocketPath}");

        public static async Task<LanTestServer> StartAsync(
            WebSocketServerOptions? webSocketOptions = null)
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
                    options: webSocketOptions);
                var pipeline = new EventPipeline(
                    new InMemoryEventStore(),
                    new CodexEventTransformer(),
                    new WebSocketEventPublisher(manager));
                await pipeline.InitializeAsync(CancellationToken.None);
                var port = FindAvailablePort();
                application = LanHost.BuildForTesting(
                    IPAddress.Loopback,
                    port,
                    pairing,
                    pipeline,
                    manager,
                    logger);
                await application.StartAsync();
                return new LanTestServer(
                    directory,
                    port,
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

        public async Task<ClientWebSocket> ConnectWithQueryAsync()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(
                new Uri($"{WebSocketAddress}?access_token={Pairing.Token.Value}"),
                CancellationToken.None);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.CloseAllAsync(CancellationToken.None);
            try
            {
                await Application.StopAsync();
            }
            finally
            {
                await Application.DisposeAsync();
                Pairing.Dispose();
                DeleteDirectory(_directory);
            }
        }

        private static int FindAvailablePort()
        {
            while (true)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (port != DesktopHost.ListenPort && !LanPortRange.Contains(port))
                {
                    return port;
                }
            }
        }
    }
}
