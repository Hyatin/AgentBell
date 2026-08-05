using System.Net.WebSockets;
using System.Text;

namespace AgentBell.Desktop.Tests;

public sealed class WebSocketConnectionManagerTests
{
    [Fact]
    public async Task Publish_SlowClientQueueIsBoundedAndDoesNotAffectAnotherClient()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-WS-Manager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var pairing = await TestPairingFactory.CreateAsync(directory);
            var logger = new CollectingDesktopDiagnosticLogger();
            var manager = new WebSocketConnectionManager(
                logger,
                options: new WebSocketServerOptions
                {
                    MaximumClients = 5,
                    QueueCapacity = 5,
                    PingInterval = TimeSpan.FromSeconds(5),
                    ClientTimeout = TimeSpan.FromSeconds(15),
                    InitialResumeGracePeriod = TimeSpan.FromMilliseconds(1),
                });
            var pipeline = new EventPipeline(
                new InMemoryEventStore(),
                new CodexEventTransformer());
            await pipeline.InitializeAsync(CancellationToken.None);
            using var slowSocket = new TestWebSocket(blockSends: true);
            using var healthySocket = new TestWebSocket(blockSends: false);
            var slowTask = manager.RunClientAsync(
                slowSocket,
                pairing,
                pipeline,
                CancellationToken.None);
            var healthyTask = manager.RunClientAsync(
                healthySocket,
                pairing,
                pipeline,
                CancellationToken.None);
            await slowSocket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => manager.ActiveConnectionCount == 2);

            for (var sequence = 1; sequence <= 20; sequence++)
            {
                manager.Publish(TestEventFactory.Create($"event-{sequence}", sequence));
                await Task.Delay(2);
            }

            await WaitUntilAsync(() => slowSocket.State == WebSocketState.Aborted);
            Assert.NotEqual(WebSocketState.Aborted, healthySocket.State);
            Assert.Contains(logger.Events, item => item.Result == "slow_client");

            await manager.CloseAllAsync(CancellationToken.None);
            await Task.WhenAll(slowTask, healthyTask).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, manager.ActiveConnectionCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TestWebSocket : WebSocket
    {
        private readonly bool _blockSends;
        private readonly TaskCompletionSource _closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public TestWebSocket(bool blockSends)
        {
            _blockSends = blockSends;
        }

        public TaskCompletionSource SendStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _closed.TrySetResult();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            if (_state is not WebSocketState.Closed and not WebSocketState.Aborted)
            {
                Abort();
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                await _closed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            return new WebSocketReceiveResult(
                0,
                WebSocketMessageType.Close,
                true,
                CloseStatus,
                CloseStatusDescription);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            if (!_blockSends)
            {
                return Task.CompletedTask;
            }

            return _closed.Task.WaitAsync(cancellationToken);
        }
    }
}
