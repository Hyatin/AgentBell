using System.Text;
using System.Text.Json;

namespace AgentBell.Hook.Tests;

public sealed class HookInputResolverTests
{
    [Fact]
    public async Task RunAsync_ValidPayloadFile_UsesNormalParserAndForwardsExactJson()
    {
        const string Json = """
            {
              "type":"agent-turn-complete",
              "thread-id":"private-file-thread-id",
              "turn-id":"private-file-turn-id",
              "cwd":"C:\\Private\\FileProject",
              "input-messages":["private file prompt"],
              "last-assistant-message":"private file response"
            }
            """;
        var path = CreateTempFile(Encoding.UTF8.GetBytes(Json));

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(Json, forwarder.RawJson);
            Assert.Equal(1, forwarder.CallCount);

            var diagnosticJson = JsonSerializer.Serialize(logger.Event);
            Assert.DoesNotContain(path, diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private-file-thread-id", diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private-file-turn-id", diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Private\\FileProject", diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private file prompt", diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private file response", diagnosticJson, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_MissingPayloadFile_ReturnsStableErrorAndExitCodeZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-missing-{Guid.NewGuid():N}.json");
        var forwarder = new StubForwarder();
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var exitCode = await application.RunAsync(
            [HookInputResolver.PayloadFileOption, path],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal(HookErrorCodes.PayloadFileNotFound, logger.Event?.Result);
        Assert.DoesNotContain(path, JsonSerializer.Serialize(logger.Event), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmptyPayloadFile_ReturnsStableErrorAndExitCodeZero()
    {
        var path = CreateTempFile([]);

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, forwarder.CallCount);
            Assert.Equal(HookErrorCodes.PayloadFileEmpty, logger.Event?.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidJsonPayloadFile_UsesNormalParserError()
    {
        var path = CreateTempFile(Encoding.UTF8.GetBytes("{\"type\":"));

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, forwarder.CallCount);
            Assert.Equal(HookErrorCodes.InvalidJson, logger.Event?.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_OversizedPayloadFile_ReturnsStableErrorAndExitCodeZero()
    {
        var path = CreateTempFile(new byte[HookInputResolver.MaxPayloadFileBytes + 1]);

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, forwarder.CallCount);
            Assert.Equal(HookErrorCodes.PayloadFileTooLarge, logger.Event?.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_Utf8ChineseAndEmojiPayloadFile_PreservesJson()
    {
        const string Json = """
            {"type":"agent-turn-complete","last-assistant-message":"中文提醒 🔔👩🏽‍💻"}
            """;
        var path = CreateTempFile(Encoding.UTF8.GetBytes(Json));

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(Json, forwarder.RawJson);
            Assert.True(logger.Event?.HasAssistantMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_PayloadFileWithoutPath_ReturnsStableErrorAndExitCodeZero()
    {
        var forwarder = new StubForwarder();
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var exitCode = await application.RunAsync(
            [HookInputResolver.PayloadFileOption],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal(HookErrorCodes.PayloadFilePathMissing, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8PayloadFile_ReturnsStableErrorAndExitCodeZero()
    {
        var path = CreateTempFile([0xFF, 0xFE, 0x00]);

        try
        {
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, forwarder.CallCount);
            Assert.Equal(HookErrorCodes.PayloadFileInvalidUtf8, logger.Event?.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_LockedPayloadFile_ReturnsStableUnreadableErrorAndExitCodeZero()
    {
        var path = CreateTempFile(Encoding.UTF8.GetBytes("{\"type\":\"agent-turn-complete\"}"));

        try
        {
            using var lockedStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var forwarder = new StubForwarder();
            var logger = new CollectingDiagnosticLogger();
            var application = CreateApplication(forwarder, logger);

            var exitCode = await application.RunAsync(
                [HookInputResolver.PayloadFileOption, path],
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, forwarder.CallCount);
            Assert.Equal(HookErrorCodes.PayloadFileUnreadable, logger.Event?.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HookApplication CreateApplication(
        StubForwarder forwarder,
        CollectingDiagnosticLogger logger) =>
        new(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            forwarder,
            logger);

    private static string CreateTempFile(byte[] contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-Hook-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private sealed class StubForwarder : IEventForwarder
    {
        public int CallCount { get; private set; }

        public string? RawJson { get; private set; }

        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
        {
            CallCount++;
            RawJson = rawJson;
            return Task.FromResult(ForwardResult.Accepted(202));
        }
    }

    private sealed class CollectingDiagnosticLogger : IDiagnosticLogger
    {
        public HookDiagnosticEvent? Event { get; private set; }

        public void Record(HookDiagnosticEvent diagnosticEvent)
        {
            Event = diagnosticEvent;
        }
    }
}
