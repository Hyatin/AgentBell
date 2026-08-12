using System.Text;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook.Tests;

public sealed class PostToolUseHookTests
{
    [Fact]
    public async Task RunAsync_ValidRequest_ForwardsOnlySanitizedCorrelationAndStaysSilent()
    {
        const string Json = """
            {
              "hook_event_name":"PostToolUse",
              "session_id":"private-session-id",
              "turn_id":"private-turn-id",
              "tool_use_id":"private-tool-use-id",
              "cwd":"C:\\Private\\AgentBell",
              "tool_name":"Bash",
              "tool_input":{"command":"private-command-value"},
              "tool_response":{"output":"private-output-value"}
            }
            """;
        var forwarder = new CapturingForwarder();
        var logger = new CollectingLogger();
        var application = CreateApplication(forwarder, logger);
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        using var output = new StringWriter();

        var exitCode = await application.RunAsync(
            [HookInputResolver.CodexPostToolUseHookOption],
            input,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.NotNull(forwarder.Json);
        using var document = JsonDocument.Parse(forwarder.Json);
        var root = document.RootElement;
        Assert.Equal("codex-post-tool-use", root.GetProperty("eventType").GetString());
        Assert.Equal("command", root.GetProperty("toolCategory").GetString());
        Assert.Equal(12, root.GetProperty("sessionIdHash").GetString()?.Length);
        Assert.Equal(12, root.GetProperty("turnIdHash").GetString()?.Length);
        Assert.Equal(12, root.GetProperty("toolUseIdHash").GetString()?.Length);
        Assert.DoesNotContain("private-command-value", forwarder.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-output-value", forwarder.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private", forwarder.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-id", forwarder.Json, StringComparison.Ordinal);
        Assert.Equal("codex-post-tool-use", logger.Event?.EventType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"hook_event_name\":\"PermissionRequest\"}")]
    public async Task RunAsync_InvalidRequest_ExitsZeroWithoutOutputOrForwarding(string json)
    {
        var forwarder = new CapturingForwarder();
        var application = CreateApplication(forwarder, new CollectingLogger());
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = new StringWriter();

        var exitCode = await application.RunAsync(
            [HookInputResolver.CodexPostToolUseHookOption],
            input,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Null(forwarder.Json);
    }

    [Fact]
    public void Parser_IgnoresToolContentAndKeepsOnlyCorrelationFields()
    {
        const string Json = """
            {"hook_event_name":"PostToolUse","turn_id":"turn","tool_use_id":"tool",
             "tool_name":"apply_patch","tool_input":{"command":"private"},
             "tool_response":{"result":"private"},"unknown":"private"}
            """;

        var result = new CodexPostToolUsePayloadParser().Parse(Json);

        Assert.True(result.IsSuccess);
        Assert.Equal("turn", result.Payload?.TurnId);
        Assert.Equal("tool", result.Payload?.ToolUseId);
        Assert.Equal("apply_patch", result.Payload?.ToolName);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result.Payload), StringComparison.Ordinal);
    }

    private static HookApplication CreateApplication(
        CapturingForwarder forwarder,
        CollectingLogger logger) => new(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new CodexPermissionRequestPayloadParser(),
            new PermissionRequestSanitizer(),
            new CodexPostToolUsePayloadParser(),
            new PostToolUseSanitizer(),
            forwarder,
            logger);

    private sealed class CapturingForwarder : IEventForwarder
    {
        public string? Json { get; private set; }

        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
        {
            Json = rawJson;
            return Task.FromResult(ForwardResult.Accepted(202));
        }
    }

    private sealed class CollectingLogger : IDiagnosticLogger
    {
        public HookDiagnosticEvent? Event { get; private set; }

        public void Record(HookDiagnosticEvent diagnosticEvent) => Event = diagnosticEvent;
    }
}
