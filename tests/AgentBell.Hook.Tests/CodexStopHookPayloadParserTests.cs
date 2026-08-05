namespace AgentBell.Hook.Tests;

public sealed class CodexStopHookPayloadParserTests
{
    private readonly CodexStopHookPayloadParser _parser = new();

    [Fact]
    public void Parse_FullStopPayload_MapsDocumentedFieldsAndIgnoresTranscriptPath()
    {
        const string Json = """
            {
              "session_id":"session-123",
              "turn_id":"turn-456",
              "cwd":"C:\\Projects\\AgentBell",
              "hook_event_name":"Stop",
              "last_assistant_message":"Completed the turn.",
              "stop_hook_active":false,
              "permission_mode":"default",
              "model":"gpt-5.6",
              "transcript_path":"C:\\Private\\transcript.json",
              "future_field":{"ignored":true}
            }
            """;

        var result = _parser.Parse(Json);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Payload);
        Assert.Equal("session-123", result.Payload.SessionId);
        Assert.Equal("turn-456", result.Payload.TurnId);
        Assert.Equal("C:\\Projects\\AgentBell", result.Payload.WorkingDirectory);
        Assert.Equal("Stop", result.Payload.HookEventName);
        Assert.Equal("Completed the turn.", result.Payload.LastAssistantMessage);
        Assert.False(result.Payload.StopHookActive);
        Assert.Equal("default", result.Payload.PermissionMode);
        Assert.Equal("gpt-5.6", result.Payload.Model);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Parse_ChineseAndEmoji_PreservesText()
    {
        const string Json = """
            {"hook_event_name":"Stop","last_assistant_message":"已完成 🔔👩🏽‍💻","model":"模型-测试"}
            """;

        var result = _parser.Parse(Json);

        Assert.True(result.IsSuccess);
        Assert.Equal("已完成 🔔👩🏽‍💻", result.Payload?.LastAssistantMessage);
        Assert.Equal("模型-测试", result.Payload?.Model);
    }

    [Fact]
    public void Parse_MissingOptionalFields_SucceedsWithNullValues()
    {
        var result = _parser.Parse("{\"hook_event_name\":\"Stop\"}");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Payload);
        Assert.Null(result.Payload.SessionId);
        Assert.Null(result.Payload.TurnId);
        Assert.Null(result.Payload.WorkingDirectory);
        Assert.Null(result.Payload.LastAssistantMessage);
        Assert.Null(result.Payload.StopHookActive);
        Assert.Null(result.Payload.PermissionMode);
        Assert.Null(result.Payload.Model);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsStableError()
    {
        var result = _parser.Parse("{\"hook_event_name\":");

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }

    [Fact]
    public void Parse_NonObjectJson_ReturnsStableError()
    {
        var result = _parser.Parse("[]");

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }

    [Fact]
    public void Parse_MissingHookEventName_ReturnsStableError()
    {
        var result = _parser.Parse("{\"session_id\":\"session-123\"}");

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.MissingHookEventName, result.ErrorCode);
    }

    [Fact]
    public void Parse_NonStopHookEvent_ReturnsStableError()
    {
        var result = _parser.Parse("{\"hook_event_name\":\"PostToolUse\"}");

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.UnsupportedHookEvent, result.ErrorCode);
    }

    [Fact]
    public void Parse_WrongKnownFieldType_ReturnsStableError()
    {
        const string Json = """
            {"hook_event_name":"Stop","stop_hook_active":"not-a-boolean"}
            """;

        var result = _parser.Parse(Json);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }
}

