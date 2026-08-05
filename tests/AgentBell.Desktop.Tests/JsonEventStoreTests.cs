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
                new JsonEventStore(path),
                new CodexEventTransformer());
            await pipeline.InitializeAsync(CancellationToken.None);

            await pipeline.AcceptAsync(
                new CodexStopHookPayload
                {
                    HookEventName = "Stop",
                    SessionId = SessionId,
                    TurnId = TurnId,
                    WorkingDirectory = FullPath,
                    LastAssistantMessage = fullMessage,
                },
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
