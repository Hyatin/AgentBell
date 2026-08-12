using System.Globalization;
using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class CodexEventTransformerTests
{
    [Fact]
    public void Transform_FullPayload_CreatesSanitizedDeterministicEvent()
    {
        var transformer = new CodexEventTransformer();
        var payload = new CodexStopHookPayload
        {
            HookEventName = "Stop",
            SessionId = "stop-session-stable",
            TurnId = "stop-turn-stable",
            WorkingDirectory = "C:\\Private\\AgentBell",
            LastAssistantMessage = "  完成第一行\r\n\t第二行 🔔  ",
        };

        var result = transformer.Transform(payload);

        Assert.Equal("codex", result.Agent);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Codex turn completed", result.Title);
        Assert.Equal(AgentEventCategories.Completion, result.Category);
        Assert.Equal(AgentActionTypes.None, result.ActionType);
        Assert.Equal("AgentBell", result.Project);
        Assert.Equal("完成第一行 第二行 🔔", result.Summary);
        Assert.Equal("791be4077488", result.ThreadIdHash);
        Assert.Equal("401f953d3fd9", result.TurnIdHash);
        Assert.Equal("codex:791be4077488:401f953d3fd9", result.EventId);
        Assert.DoesNotContain("stop-session-stable", result.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-turn-stable", result.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private", result.Project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Projects\\AgentBell", "AgentBell")]
    [InlineData("C:\\Projects\\AgentBell\\", "AgentBell")]
    [InlineData("/work/AgentBell", "AgentBell")]
    [InlineData("\\\\server\\share\\AgentBell", "AgentBell")]
    [InlineData("C:\\", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    public void ExtractProject_ReturnsOnlyFinalSegment(string path, string? expected)
    {
        Assert.Equal(expected, CodexEventTransformer.ExtractProject(path));
    }

    [Fact]
    public void CreateSummary_TruncatesAt160UnicodeTextElementsWithoutSplittingEmoji()
    {
        var input = new string('a', 159) + "👩🏽‍💻" + "should-not-remain";

        var result = CodexEventTransformer.CreateSummary(input);

        Assert.NotNull(result);
        Assert.Equal(160, new StringInfo(result).LengthInTextElements);
        Assert.EndsWith("👩🏽‍💻", result, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-remain", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Transform_MissingOptionalFields_UsesNoRawIdentifierOrPrivateContent()
    {
        var result = new CodexEventTransformer().Transform(
            new CodexStopHookPayload { HookEventName = "Stop" });

        Assert.StartsWith("codex-local:", result.EventId, StringComparison.Ordinal);
        Assert.Null(result.Project);
        Assert.Null(result.Summary);
        Assert.Null(result.ThreadIdHash);
        Assert.Null(result.TurnIdHash);
    }

    [Fact]
    public void Transform_SessionWithoutTurn_DoesNotFalselyDeduplicateFutureTurns()
    {
        var transformer = new CodexEventTransformer();
        var payload = new CodexStopHookPayload
        {
            HookEventName = "Stop",
            SessionId = "same-session-without-turn",
        };

        var first = transformer.Transform(payload);
        var second = transformer.Transform(payload);

        Assert.NotEqual(first.EventId, second.EventId);
        Assert.NotNull(first.ThreadIdHash);
        Assert.Null(first.TurnIdHash);
    }
}
