using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class CodexNotifyPayloadTests
{
    [Fact]
    public void Deserialize_FullPayload_MapsDocumentedFields()
    {
        const string Json = """
            {
              "type": "agent-turn-complete",
              "thread-id": "thread-123",
              "turn-id": "turn-456",
              "cwd": "C:\\Projects\\AgentBell",
              "input-messages": ["Implement the probe."],
              "last-assistant-message": "Implemented."
            }
            """;

        var payload = JsonSerializer.Deserialize<CodexNotifyPayload>(Json);

        Assert.NotNull(payload);
        Assert.Equal("agent-turn-complete", payload.Type);
        Assert.Equal("thread-123", payload.ThreadId);
        Assert.Equal("turn-456", payload.TurnId);
        Assert.Equal("C:\\Projects\\AgentBell", payload.WorkingDirectory);
        Assert.Equal(["Implement the probe."], payload.InputMessages);
        Assert.Equal("Implemented.", payload.LastAssistantMessage);
    }

    [Fact]
    public void Deserialize_MissingOptionalFields_LeavesThemNull()
    {
        var payload = JsonSerializer.Deserialize<CodexNotifyPayload>("{\"type\":\"agent-turn-complete\"}");

        Assert.NotNull(payload);
        Assert.Null(payload.ThreadId);
        Assert.Null(payload.TurnId);
        Assert.Null(payload.WorkingDirectory);
        Assert.Null(payload.InputMessages);
        Assert.Null(payload.LastAssistantMessage);
    }

    [Fact]
    public void Deserialize_UnknownFields_IgnoresThem()
    {
        var payload = JsonSerializer.Deserialize<CodexNotifyPayload>(
            "{\"type\":\"agent-turn-complete\",\"future-field\":{\"nested\":true}}");

        Assert.NotNull(payload);
        Assert.Equal("agent-turn-complete", payload.Type);
    }

    [Fact]
    public void Deserialize_ChineseAndEmoji_PreservesText()
    {
        const string Json = """
            {
              "type": "agent-turn-complete",
              "input-messages": ["完成事件探针 🔔"],
              "last-assistant-message": "已完成 ✅👩🏽‍💻"
            }
            """;

        var payload = JsonSerializer.Deserialize<CodexNotifyPayload>(Json);

        Assert.NotNull(payload);
        Assert.Equal(["完成事件探针 🔔"], payload.InputMessages);
        Assert.Equal("已完成 ✅👩🏽‍💻", payload.LastAssistantMessage);
    }
}

