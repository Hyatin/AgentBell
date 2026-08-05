using System.Text;
using System.Text.Json;

namespace AgentBell.Hook.Tests;

public sealed class StopHookApplicationTests
{
    [Fact]
    public async Task RunAsync_NormalStopStdin_MapsMetadataAndForwardsExactJson()
    {
        const string Json = """
            {
              "session_id":"stop-session-stable",
              "turn_id":"stop-turn-stable",
              "cwd":"C:\\Private\\AgentBell",
              "hook_event_name":"Stop",
              "last_assistant_message":"private assistant message",
              "stop_hook_active":false,
              "permission_mode":"default",
              "model":"gpt-5.6"
            }
            """;
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(application, Encoding.UTF8.GetBytes(Json));

        AssertContinueResponse(runResult);
        Assert.DoesNotContain(Json, runResult.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-session-stable", runResult.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-turn-stable", runResult.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private\\AgentBell", runResult.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("private assistant message", runResult.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(Json, forwarder.RawJson);
        Assert.Equal(1, forwarder.CallCount);
        Assert.Equal("codex-stop", logger.Event?.EventType);
        Assert.Equal("791be4077488", logger.Event?.ThreadIdHash);
        Assert.Equal("401f953d3fd9", logger.Event?.TurnIdHash);
        Assert.True(logger.Event?.HasWorkingDirectory);
        Assert.True(logger.Event?.HasAssistantMessage);

        var diagnosticJson = JsonSerializer.Serialize(logger.Event);
        Assert.DoesNotContain(Json, diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-session-stable", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-turn-stable", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private\\AgentBell", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private assistant message", diagnosticJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ChineseAndEmojiStopStdin_PreservesForwardedJsonButNotDiagnosticContent()
    {
        const string Json = """
            {"hook_event_name":"Stop","last_assistant_message":"中文完成 🔔👩🏽‍💻"}
            """;
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(application, Encoding.UTF8.GetBytes(Json));

        AssertContinueResponse(runResult);
        Assert.Equal(Json, forwarder.RawJson);
        Assert.True(logger.Event?.HasAssistantMessage);
        Assert.DoesNotContain(
            "中文完成 🔔👩🏽‍💻",
            JsonSerializer.Serialize(logger.Event),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmptyStopStdin_ReturnsStableErrorAndExitCodeZero()
    {
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(application, []);

        AssertContinueResponse(runResult);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal("codex-stop", logger.Event?.EventType);
        Assert.Equal(HookErrorCodes.StopHookEmptyInput, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_InvalidStopJson_ReturnsStableErrorAndExitCodeZero()
    {
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(
            application,
            Encoding.UTF8.GetBytes("{\"hook_event_name\":"));

        AssertContinueResponse(runResult);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal("codex-stop", logger.Event?.EventType);
        Assert.Equal(HookErrorCodes.InvalidJson, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_OversizedStopStdin_ReturnsStableErrorAndExitCodeZero()
    {
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);
        var bytes = new byte[HookInputResolver.MaxInputBytes + 1];

        var runResult = await RunStopAsync(application, bytes);

        AssertContinueResponse(runResult);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal("codex-stop", logger.Event?.EventType);
        Assert.Equal(HookErrorCodes.StopHookInputTooLarge, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_NonStopHookEvent_ReturnsStableErrorAndExitCodeZero()
    {
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(
            application,
            Encoding.UTF8.GetBytes("{\"hook_event_name\":\"PostToolUse\"}"));

        AssertContinueResponse(runResult);
        Assert.Equal(0, forwarder.CallCount);
        Assert.Equal("codex-stop", logger.Event?.EventType);
        Assert.Equal(HookErrorCodes.UnsupportedHookEvent, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_StopWithoutOptionalFields_StillForwardsAndMapsNullMetadata()
    {
        const string Json = "{\"hook_event_name\":\"Stop\"}";
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(application, Encoding.UTF8.GetBytes(Json));

        AssertContinueResponse(runResult);
        Assert.Equal(Json, forwarder.RawJson);
        Assert.Null(logger.Event?.ThreadIdHash);
        Assert.Null(logger.Event?.TurnIdHash);
        Assert.False(logger.Event?.HasWorkingDirectory);
        Assert.False(logger.Event?.HasAssistantMessage);
    }

    [Theory]
    [InlineData(HookErrorCodes.ForwardUnavailable)]
    [InlineData(HookErrorCodes.ForwardTimeout)]
    public async Task RunAsync_ForwardFailure_RecordsStableResultAndExitCodeZero(string errorCode)
    {
        var forwarder = new StubForwarder(ForwardResult.Failed(errorCode));
        var logger = new CollectingDiagnosticLogger();
        var application = CreateApplication(forwarder, logger);

        var runResult = await RunStopAsync(
            application,
            Encoding.UTF8.GetBytes("{\"hook_event_name\":\"Stop\"}"));

        AssertContinueResponse(runResult);
        Assert.Equal(errorCode, logger.Event?.Result);
    }

    private static void AssertContinueResponse(StopRunResult runResult)
    {
        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal(HookApplication.StopHookContinueResponse, runResult.StandardOutput);
    }

    private static async Task<StopRunResult> RunStopAsync(
        HookApplication application,
        byte[] stdinBytes)
    {
        await using var stream = new MemoryStream(stdinBytes, writable: false);
        using var standardOutput = new StringWriter();
        var exitCode = await application.RunAsync(
            [HookInputResolver.CodexStopHookOption],
            stream,
            standardOutput,
            CancellationToken.None);

        return new StopRunResult(exitCode, standardOutput.ToString());
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

    private sealed class StubForwarder(ForwardResult result) : IEventForwarder
    {
        public int CallCount { get; private set; }

        public string? RawJson { get; private set; }

        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
        {
            CallCount++;
            RawJson = rawJson;
            return Task.FromResult(result);
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

    private sealed record StopRunResult(int ExitCode, string StandardOutput);
}
