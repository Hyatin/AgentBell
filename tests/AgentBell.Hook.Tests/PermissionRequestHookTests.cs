using System.Text;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook.Tests;

public sealed class PermissionRequestHookTests
{
    [Fact]
    public async Task RunAsync_ValidRequest_ForwardsOnlySanitizedPayloadAndStaysSilent()
    {
        const string RawCommand = "private-command-value";
        const string Description = "private-description-value";
        const string Json = """
            {
              "hook_event_name":"PermissionRequest",
              "session_id":"private-session-id",
              "turn_id":"private-turn-id",
              "tool_use_id":"private-tool-use-id",
              "cwd":"C:\\Private\\AgentBell",
              "permission_mode":"ask",
              "tool_name":"Bash",
              "tool_input":{
                "command":"private-command-value",
                "description":"private-description-value"
              }
            }
            """;
        var forwarder = new CapturingForwarder(ForwardResult.Accepted(202));
        var logger = new CollectingLogger();
        var app = CreateApplication(forwarder, logger);
        await using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        using var stdout = new StringWriter();

        var exitCode = await app.RunAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            stdin,
            stdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotNull(forwarder.Json);
        var forwarded = forwarder.Json;
        using var document = JsonDocument.Parse(forwarded);
        var root = document.RootElement;
        Assert.Equal("codex-permission-request", root.GetProperty("eventType").GetString());
        Assert.Equal("action_required", root.GetProperty("category").GetString());
        Assert.Equal("permission_required", root.GetProperty("actionType").GetString());
        Assert.Equal("command", root.GetProperty("toolCategory").GetString());
        Assert.Equal("AgentBell", root.GetProperty("project").GetString());
        Assert.Equal(12, root.GetProperty("sessionIdHash").GetString()?.Length);
        Assert.Equal(12, root.GetProperty("turnIdHash").GetString()?.Length);
        Assert.Equal(12, root.GetProperty("toolUseIdHash").GetString()?.Length);
        Assert.DoesNotContain(RawCommand, forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain(Description, forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-id", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn-id", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("allow", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deny", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedInput", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("codex-permission-request", logger.Event?.EventType);
        Assert.Equal("command", logger.Event?.ToolCategory);
        var diagnostic = JsonSerializer.Serialize(logger.Event);
        Assert.DoesNotContain(RawCommand, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Description, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-id", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bash", "command")]
    [InlineData("apply_patch", "file_change")]
    [InlineData("Edit", "file_change")]
    [InlineData("Write", "file_change")]
    [InlineData("mcp__server__tool", "external_tool")]
    [InlineData("computer_use", "computer_control")]
    [InlineData("network_approval", "network_access")]
    [InlineData("future_tool", "other")]
    [InlineData(null, "other")]
    public void ToolName_MapsToAllowListedCategory(string? toolName, string expected)
    {
        Assert.Equal(expected, PermissionToolCategoryMapper.Map(toolName));
    }

    [Fact]
    public void Sanitizer_SameIdentifiers_AreStableAcrossInstances()
    {
        var payload = new CodexPermissionRequestPayload
        {
            HookEventName = "PermissionRequest",
            SessionId = "stable-session",
            TurnId = "stable-turn",
            ToolUseId = "stable-tool-use",
            ToolName = "Bash",
        };

        var first = new PermissionRequestSanitizer().Sanitize(payload).Event;
        var second = new PermissionRequestSanitizer().Sanitize(payload).Event;

        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.SessionIdHash, second.SessionIdHash);
        Assert.Equal(first.TurnIdHash, second.TurnIdHash);
        Assert.Equal(first.ToolUseIdHash, second.ToolUseIdHash);
    }

    [Fact]
    public void Sanitizer_MissingAllIdentifiers_DoesNotCollapseUnrelatedRequests()
    {
        var payload = new CodexPermissionRequestPayload
        {
            HookEventName = "PermissionRequest",
            ToolName = "Bash",
        };

        var first = new PermissionRequestSanitizer().Sanitize(payload).Event;
        var second = new PermissionRequestSanitizer().Sanitize(payload).Event;

        Assert.NotEqual(first.EventId, second.EventId);
        Assert.Matches("^codex-action:[0-9a-f]{24}$", first.EventId);
        Assert.Matches("^codex-action:[0-9a-f]{24}$", second.EventId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"hook_event_name\":\"Stop\"}")]
    public async Task RunAsync_InvalidRequest_ExitsZeroWithoutOutputOrForwarding(string json)
    {
        var forwarder = new CapturingForwarder(ForwardResult.Accepted(202));
        var app = CreateApplication(forwarder, new CollectingLogger());
        await using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var stdout = new StringWriter();

        var exitCode = await app.RunAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            stdin,
            stdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Null(forwarder.Json);
    }

    [Theory]
    [InlineData(HookErrorCodes.ForwardUnavailable)]
    [InlineData(HookErrorCodes.ForwardTimeout)]
    public async Task RunAsync_ForwardFailure_StillExitsZeroAndStaysSilent(string failureCode)
    {
        var forwarder = new CapturingForwarder(
            ForwardResult.Failed(failureCode));
        var logger = new CollectingLogger();
        var app = CreateApplication(forwarder, logger);
        await using var stdin = new MemoryStream(
            Encoding.UTF8.GetBytes(
                "{\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\"}"));
        using var stdout = new StringWriter();

        var exitCode = await app.RunAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            stdin,
            stdout,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(failureCode, logger.Event?.Result);
    }

    private static HookApplication CreateApplication(
        CapturingForwarder forwarder,
        CollectingLogger logger) => new(
            new HookInputResolver(),
            new CodexPayloadParser(),
            new CodexStopHookPayloadParser(),
            new CodexPermissionRequestPayloadParser(),
            new PermissionRequestSanitizer(),
            forwarder,
            logger);

    private sealed class CapturingForwarder(ForwardResult result) : IEventForwarder
    {
        public string? Json { get; private set; }

        public Task<ForwardResult> ForwardAsync(string rawJson, CancellationToken cancellationToken)
        {
            Json = rawJson;
            return Task.FromResult(result);
        }
    }

    private sealed class CollectingLogger : IDiagnosticLogger
    {
        public HookDiagnosticEvent? Event { get; private set; }

        public void Record(HookDiagnosticEvent diagnosticEvent) => Event = diagnosticEvent;
    }
}
