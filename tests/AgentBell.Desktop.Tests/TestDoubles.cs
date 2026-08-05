using AgentBell.Contracts;
using System.Security.Cryptography;

namespace AgentBell.Desktop.Tests;

internal sealed class InMemoryEventStore : IEventStore
{
    private readonly object _gate = new();
    private IReadOnlyList<AgentEvent> _loadedEvents;

    public InMemoryEventStore(IReadOnlyList<AgentEvent>? loadedEvents = null)
    {
        _loadedEvents = loadedEvents ?? [];
    }

    public bool SaveSucceeds { get; set; } = true;

    public int SaveCount { get; private set; }

    public IReadOnlyList<AgentEvent> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _loadedEvents.ToArray();
            }
        }
    }

    public Task<EventStoreLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(
                new EventStoreLoadResult(_loadedEvents.ToArray(), true, false));
        }
    }

    public Task<bool> SaveAsync(
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SaveCount++;
            if (SaveSucceeds)
            {
                _loadedEvents = events.ToArray();
            }

            return Task.FromResult(SaveSucceeds);
        }
    }
}

internal sealed class CollectingDesktopDiagnosticLogger : IDesktopDiagnosticLogger
{
    private readonly object _gate = new();
    private readonly List<DesktopDiagnosticEvent> _events = [];

    public IReadOnlyList<DesktopDiagnosticEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void Record(DesktopDiagnosticEvent diagnosticEvent)
    {
        lock (_gate)
        {
            _events.Add(diagnosticEvent);
        }
    }
}

internal sealed class CollectingEventPublisher : IEventPublisher
{
    private readonly object _gate = new();
    private readonly List<AgentEvent> _events = [];

    public bool ThrowOnPublish { get; set; }

    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("test publisher failure");
        }

        lock (_gate)
        {
            _events.Add(agentEvent);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class TestPairingTokenProtector : IPairingTokenProtector
{
    public bool FailUnprotect { get; set; }

    public byte[] Protect(byte[] plaintext) => Transform(plaintext);

    public byte[] Unprotect(byte[] protectedData)
    {
        if (FailUnprotect)
        {
            throw new CryptographicException("test failure");
        }

        return Transform(protectedData);
    }

    private static byte[] Transform(byte[] value) =>
        value.Select(item => (byte)(item ^ 0x5A)).ToArray();
}

internal static class TestPairingFactory
{
    public static async Task<PairingConfigurationSession> CreateAsync(string directory)
    {
        var result = await new PairingConfigurationManager(
                new AgentBellConfigStore(Path.Combine(directory, "config.json")),
                new TestPairingTokenProtector(),
                deviceNameProvider: () => "AgentBell Test PC")
            .LoadOrCreateAsync(CancellationToken.None);
        return Assert.IsType<PairingConfigurationSession>(result.Session);
    }
}

internal static class TestEventFactory
{
    public static AgentEvent Create(string eventId, long sequence) =>
        new()
        {
            EventId = eventId,
            Agent = "codex",
            Status = "completed",
            Title = "Codex 已完成当前回合",
            OccurredAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            Sequence = sequence,
        };
}
