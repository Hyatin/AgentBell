namespace AgentBell.Hook.Tests;

public sealed class CodexPayloadParserTests
{
    private readonly CodexPayloadParser _parser = new();

    [Fact]
    public void Parse_NoArguments_ReturnsStableError()
    {
        var result = _parser.Parse([]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.NoArguments, result.ErrorCode);
    }

    [Fact]
    public void Parse_InvalidSingleArgument_ReturnsInvalidJson()
    {
        var result = _parser.Parse(["not-json"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }

    [Fact]
    public void Parse_MalformedJsonObject_ReturnsInvalidJson()
    {
        var result = _parser.Parse(["{\"type\":"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }

    [Fact]
    public void Parse_ObjectWithoutType_ReturnsMissingType()
    {
        var result = _parser.Parse(["{\"turn-id\":\"turn-1\"}"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.MissingType, result.ErrorCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    public void Parse_JsonThatIsNotAnObject_ReturnsJsonNotFound(string json)
    {
        var result = _parser.Parse([json]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.JsonNotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"\"")]
    public void Parse_UnusableType_ReturnsMissingType(string typeJson)
    {
        var result = _parser.Parse([$"{{\"type\":{typeJson}}}"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.MissingType, result.ErrorCode);
    }

    [Fact]
    public void Parse_UnsupportedType_ReturnsUnsupportedType()
    {
        var result = _parser.Parse(["{\"type\":\"approval-requested\"}"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.UnsupportedType, result.ErrorCode);
    }

    [Fact]
    public void Parse_SupportedMinimalPayload_Succeeds()
    {
        const string Json = "{\"type\":\"agent-turn-complete\"}";

        var result = _parser.Parse([Json]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Payload);
        Assert.Equal("agent-turn-complete", result.Payload.Type);
        Assert.Equal(Json, result.RawJson);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Parse_MultipleArguments_SearchesBackwardForTypedObject()
    {
        const string Json = "{\"type\":\"agent-turn-complete\",\"turn-id\":\"turn-9\"}";

        var result = _parser.Parse([Json, "--unrelated", "{\"other\":true}"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("turn-9", result.Payload?.TurnId);
        Assert.Equal(Json, result.RawJson);
    }

    [Fact]
    public void Parse_UsesLastTypedJsonObjectWithoutCombiningArguments()
    {
        const string First = "{\"type\":\"agent-turn-complete\",\"turn-id\":\"first\"}";
        const string Last = "{\"type\":\"agent-turn-complete\",\"turn-id\":\"last\"}";

        var result = _parser.Parse([First, "ignored", Last]);

        Assert.True(result.IsSuccess);
        Assert.Equal("last", result.Payload?.TurnId);
        Assert.Equal(Last, result.RawJson);
    }

    [Fact]
    public void Parse_UnknownFields_AreIgnored()
    {
        const string Json = """
            {"type":"agent-turn-complete","future":{"nested":true}}
            """;

        var result = _parser.Parse([Json]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Parse_WrongKnownFieldType_ReturnsInvalidJson()
    {
        const string Json = """
            {"type":"agent-turn-complete","input-messages":"private prompt"}
            """;

        var result = _parser.Parse([Json]);

        Assert.False(result.IsSuccess);
        Assert.Equal(HookErrorCodes.InvalidJson, result.ErrorCode);
    }

    [Fact]
    public void Parse_ChineseAndEmoji_PreservesOptionalText()
    {
        const string Json = """
            {"type":"agent-turn-complete","last-assistant-message":"完成 🔔👩🏽‍💻"}
            """;

        var result = _parser.Parse([Json]);

        Assert.True(result.IsSuccess);
        Assert.Equal("完成 🔔👩🏽‍💻", result.Payload?.LastAssistantMessage);
    }
}

