using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook.Tests;

public sealed class HookApplicationTests
{
    [Fact]
    public void DiagnosticEvent_SameIdentifiers_UseStableTruncatedSha256Hashes()
    {
        var payload = new CodexNotifyPayload
        {
            Type = "agent-turn-complete",
            ThreadId = "stable-thread-id",
            TurnId = "stable-turn-id",
        };

        var first = HookDiagnosticEvent.Create(
            payload,
            ForwardResult.Accepted(202),
            TimeSpan.Zero);
        var second = HookDiagnosticEvent.Create(
            payload,
            ForwardResult.Accepted(202),
            TimeSpan.Zero);

        Assert.Equal("70675336f9d7", first.ThreadIdHash);
        Assert.Equal("2a82b263530c", first.TurnIdHash);
        Assert.Equal(first.ThreadIdHash, second.ThreadIdHash);
        Assert.Equal(first.TurnIdHash, second.TurnIdHash);
        Assert.DoesNotContain(payload.ThreadId, first.ThreadIdHash, StringComparison.Ordinal);
        Assert.DoesNotContain(payload.TurnId, first.TurnIdHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ProductionSingleJsonArgument_ForwardsExactJsonAndReturnsZero()
    {
        const string Json = """
            {
              "type":"agent-turn-complete",
              "thread-id":"private-thread-id",
              "turn-id":"private-turn-id",
              "cwd":"C:\\Private\\Project",
              "input-messages":["private prompt"],
              "last-assistant-message":"private assistant response"
            }
            """;
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            forwarder,
            logger);

        var exitCode = await application.RunAsync([Json], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(Json, forwarder.RawJson);
        Assert.NotNull(logger.Event);
        Assert.Equal(ForwardResult.SuccessCode, logger.Event.Result);
        Assert.Equal(202, logger.Event.HttpStatusCode);
    }

    [Fact]
    public async Task RunAsync_DiagnosticEvent_DoesNotContainPrivateContent()
    {
        const string Json = """
            {
              "type":"agent-turn-complete",
              "thread-id":"private-thread-id",
              "turn-id":"private-turn-id",
              "cwd":"C:\\Private\\Project",
              "input-messages":["private prompt"],
              "last-assistant-message":"private assistant response"
            }
            """;
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new StubForwarder(ForwardResult.Accepted(202)),
            logger);

        await application.RunAsync([Json], CancellationToken.None);

        var diagnosticJson = JsonSerializer.Serialize(logger.Event);
        Assert.DoesNotContain("private-thread-id", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-id", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private\\Project", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private assistant response", diagnosticJson, StringComparison.Ordinal);
        Assert.True(logger.Event?.HasWorkingDirectory);
        Assert.True(logger.Event?.HasAssistantMessage);
        Assert.Equal(12, logger.Event?.ThreadIdHash?.Length);
        Assert.Equal(12, logger.Event?.TurnIdHash?.Length);
    }

    [Fact]
    public async Task RunAsync_UnsupportedEvent_DoesNotForward()
    {
        var forwarder = new StubForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            forwarder,
            logger);

        var exitCode = await application.RunAsync(
            ["{\"type\":\"other-event\"}"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(forwarder.RawJson);
        Assert.Equal(HookErrorCodes.UnsupportedType, logger.Event?.Result);
    }

    [Fact]
    public async Task RunAsync_ForwarderThrows_ContainsFailureAndReturnsZero()
    {
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new ThrowingForwarder(),
            logger);

        var exitCode = await application.RunAsync(
            ["{\"type\":\"agent-turn-complete\"}"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(HookErrorCodes.UnexpectedError, logger.Event?.Result);
        Assert.Equal("http_forward", logger.Event?.FailureStage);
        Assert.Equal(nameof(InvalidOperationException), logger.Event?.ExceptionType);
        var diagnosticJson = JsonSerializer.Serialize(logger.Event);
        Assert.DoesNotContain("sensitive exception text", diagnosticJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ForwarderCancellation_IsClassifiedAsForwardTimeout()
    {
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new CancelingForwarder(),
            logger);

        var exitCode = await application.RunAsync(
            ["{\"type\":\"agent-turn-complete\"}"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(HookErrorCodes.ForwardTimeout, logger.Event?.Result);
        Assert.Equal("http_forward", logger.Event?.FailureStage);
        Assert.Equal(nameof(OperationCanceledException), logger.Event?.ExceptionType);
    }

    [Fact]
    public async Task RunAsync_InputResolutionCanceledByHardDeadline_IsClassifiedAsForwardTimeout()
    {
        using var hardDeadline = new CancellationTokenSource();
        hardDeadline.Cancel();
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new CancelingInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new StubForwarder(ForwardResult.Accepted(202)),
            logger);

        var exitCode = await application.RunAsync(
            [HookInputResolver.CodexStopHookOption],
            Stream.Null,
            TextWriter.Null,
            hardDeadline.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(HookErrorCodes.ForwardTimeout, logger.Event?.Result);
        Assert.Equal("input_resolution", logger.Event?.FailureStage);
        Assert.Equal(nameof(OperationCanceledException), logger.Event?.ExceptionType);
    }

    [Fact]
    public async Task RunAsync_ParserUnknownException_RemainsUnexpectedError()
    {
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new ThrowingPayloadParser(),
            new CodexStopHookPayloadParser(),
            new StubForwarder(ForwardResult.Accepted(202)),
            logger);

        var exitCode = await application.RunAsync(
            ["{\"type\":\"agent-turn-complete\"}"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(HookErrorCodes.UnexpectedError, logger.Event?.Result);
        Assert.Equal("payload_parse", logger.Event?.FailureStage);
        Assert.Equal(nameof(InvalidOperationException), logger.Event?.ExceptionType);
    }

    [Fact]
    public async Task RunAsync_DiagnosticLoggerThrows_StillReturnsZero()
    {
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new StubForwarder(ForwardResult.Accepted(202)),
            new ThrowingDiagnosticLogger());

        var exitCode = await application.RunAsync(
            ["{\"type\":\"agent-turn-complete\"}"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_RejectedForward_PreservesStableResultAndStatus()
    {
        var logger = new CollectingDiagnosticLogger();
        var application = new HookApplication(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new StubForwarder(ForwardResult.Failed(HookErrorCodes.ForwardRejected, 503)),
            logger);

        await application.RunAsync(
            ["{\"type\":\"agent-turn-complete\"}"],
            CancellationToken.None);

        Assert.Equal(HookErrorCodes.ForwardRejected, logger.Event?.Result);
        Assert.Equal(503, logger.Event?.HttpStatusCode);
    }

    private sealed class StubForwarder(ForwardResult result) : IEventForwarder
    {
        public string? RawJson { get; private set; }

        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
        {
            RawJson = rawJson;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingForwarder : IEventForwarder
    {
        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive exception text");
    }

    private sealed class CancelingForwarder : IEventForwarder
    {
        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("sensitive cancellation text");
    }

    private sealed class CancelingInputResolver : IHookInputResolver
    {
        public ValueTask<HookInputResult> ResolveAsync(
            IReadOnlyList<string> arguments,
            Stream standardInput,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HookInputResult>(
                new OperationCanceledException(cancellationToken));
    }

    private sealed class ThrowingPayloadParser : ICodexPayloadParser
    {
        public CodexPayloadParseResult Parse(IReadOnlyList<string> arguments) =>
            throw new InvalidOperationException("sensitive parser details");
    }

    private sealed class CollectingDiagnosticLogger : IDiagnosticLogger
    {
        public HookDiagnosticEvent? Event { get; private set; }

        public void Record(HookDiagnosticEvent diagnosticEvent)
        {
            Event = diagnosticEvent;
        }
    }

    private sealed class ThrowingDiagnosticLogger : IDiagnosticLogger
    {
        public void Record(HookDiagnosticEvent diagnosticEvent) =>
            throw new IOException("diagnostic path failed");
    }
}
