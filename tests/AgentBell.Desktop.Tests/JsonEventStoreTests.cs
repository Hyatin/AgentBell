using System.Text;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class JsonEventStoreTests
{
    [Fact]
    public async Task SaveAsync_AtomicallyReplacesValidJsonWithoutLeavingTemporaryFiles()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "events.json");

        try
        {
            var store = new JsonEventStore(path);
            Assert.True(await store.SaveAsync(
                [TestEventFactory.Create("first", 1)],
                CancellationToken.None));
            Assert.True(await store.SaveAsync(
                [TestEventFactory.Create("second", 2)],
                CancellationToken.None));

            var load = await store.LoadAsync(CancellationToken.None);
            var item = Assert.Single(load.Events);
            Assert.Equal("second", item.EventId);
            Assert.Empty(Directory.GetFiles(directory, "events.json.tmp-*"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_QuarantinesAndRecreatesEmptyFile()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "events.json");

        try
        {
            await File.WriteAllTextAsync(path, "{invalid", new UTF8Encoding(false));
            var store = new JsonEventStore(path);

            var result = await store.LoadAsync(CancellationToken.None);

            Assert.True(result.CorruptFileRecovered);
            Assert.True(result.PersistenceSucceeded);
            Assert.Empty(result.Events);
            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory, "events.json.corrupt-*"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.Equal(0, document.RootElement.GetArrayLength());
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAsync_UnwritablePath_ReturnsFalseWithoutThrowing()
    {
        var directory = CreateTestDirectory();
        var blockingFile = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block", new UTF8Encoding(false));
        var store = new JsonEventStore(Path.Combine(blockingFile, "events.json"));

        try
        {
            var result = await store.SaveAsync(
                [TestEventFactory.Create("event", 1)],
                CancellationToken.None);

            Assert.False(result);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RealPipelinePersistence_ContainsNoRawIdsFullPathOrFullMessage()
    {
        const string SessionId = "raw-private-session-id";
        const string TurnId = "raw-private-turn-id";
        const string FullPath = "C:\\VeryPrivate\\Source\\AgentBell";
        var fullMessage = new string('私', 170) + "FULL-PRIVATE-TAIL";
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "events.json");

        try
        {
            var pipeline = new EventPipeline(
                new JsonEventStore(path));
            await pipeline.InitializeAsync(CancellationToken.None);

            await pipeline.AcceptAsync(
                new CodexPipelineSubmissionFactory().Create(new CodexStopHookPayload
                {
                    HookEventName = "Stop",
                    SessionId = SessionId,
                    TurnId = TurnId,
                    WorkingDirectory = FullPath,
                    LastAssistantMessage = fullMessage,
                }),
                CancellationToken.None);

            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(SessionId, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(TurnId, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(FullPath, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(fullMessage, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("FULL-PRIVATE-TAIL", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("input-messages", persisted, StringComparison.Ordinal);
            Assert.Contains("AgentBell", persisted, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_Valid07HistoryFixture_PreservesContentOrderIdsAndRestartSequence()
    {
        const string Fixture = """
            [
              {
                "eventId":"codex:fixture-thread-a:fixture-turn-a",
                "agent":"codex",
                "status":"completed",
                "category":"completion",
                "actionType":"none",
                "toolCategory":"none",
                "title":"Codex turn completed",
                "project":"ProjectA",
                "summary":"Synthetic completion A.",
                "threadIdHash":"fixture-thread-a",
                "turnIdHash":"fixture-turn-a",
                "toolUseIdHash":null,
                "occurredAt":"2026-08-07T00:00:00+00:00",
                "sequence":41,
                "resolvedAt":null
              },
              {
                "eventId":"codex:fixture-thread-b:fixture-turn-b",
                "agent":"codex",
                "status":"completed",
                "category":"completion",
                "actionType":"none",
                "toolCategory":"none",
                "title":"Codex turn completed",
                "project":"ProjectB",
                "summary":"Synthetic completion B.",
                "threadIdHash":"fixture-thread-b",
                "turnIdHash":"fixture-turn-b",
                "toolUseIdHash":null,
                "occurredAt":"2026-08-07T00:00:01+00:00",
                "sequence":42,
                "resolvedAt":null
              }
            ]
            """;
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "events.json");

        try
        {
            await File.WriteAllTextAsync(path, Fixture, new UTF8Encoding(false));
            var store = new JsonEventStore(path);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.False(loaded.CorruptFileRecovered);
            Assert.Equal([41L, 42L], loaded.Events.Select(item => item.Sequence));
            Assert.Equal(
                [
                    "codex:fixture-thread-a:fixture-turn-a",
                    "codex:fixture-thread-b:fixture-turn-b",
                ],
                loaded.Events.Select(item => item.EventId));
            Assert.All(loaded.Events, item => Assert.Equal("codex", item.Agent));

            await using var pipeline = new EventPipeline(store);
            await pipeline.InitializeAsync(CancellationToken.None);
            var accepted = await pipeline.AcceptAsync(
                new CodexPipelineSubmissionFactory().Create(new CodexStopHookPayload
                {
                    HookEventName = "Stop",
                    SessionId = "restart-session",
                    TurnId = "restart-turn",
                    LastAssistantMessage = "Synthetic restart completion.",
                }),
                CancellationToken.None);

            Assert.Equal(43, Assert.IsType<AgentEvent>(accepted.Event).Sequence);
            Assert.Equal(3, (await pipeline.GetHistoryAsync(0, CancellationToken.None)).EventCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_Malformed07Entry_UsesCurrentCorruptRecoveryBehavior()
    {
        const string Fixture = """
            [{"eventId":"missing-required-fields","sequence":1}]
            """;
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "events.json");

        try
        {
            await File.WriteAllTextAsync(path, Fixture, new UTF8Encoding(false));

            var loaded = await new JsonEventStore(path).LoadAsync(CancellationToken.None);

            Assert.True(loaded.CorruptFileRecovered);
            Assert.Empty(loaded.Events);
            Assert.Single(Directory.GetFiles(directory, "events.json.corrupt-*"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-Desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
