using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class CodexActionRequestClassifierTests
{
    [Theory]
    [InlineData("I need your approval before running this.", AgentActionTypes.PermissionRequired)]
    [InlineData("请选择 A 或 B。", AgentActionTypes.InputRequired)]
    [InlineData("Which option should I use for the implementation?", AgentActionTypes.InputRequired)]
    [InlineData("Please confirm before I continue.", AgentActionTypes.ConfirmationRequired)]
    [InlineData("我可以继续吗？", AgentActionTypes.ConfirmationRequired)]
    [InlineData("I cannot continue until the missing file is supplied.", AgentActionTypes.AttentionRequired)]
    [InlineData("当前需要你处理；I cannot continue until it is fixed.", AgentActionTypes.AttentionRequired)]
    public void Classify_ExplicitRequest_ReturnsHighConfidenceAction(
        string message,
        string expected)
    {
        var result = new CodexActionRequestClassifier().Classify(message);

        Assert.Equal(expected, result.ActionType);
        Assert.Equal("high", result.ConfidenceBand);
        Assert.False(string.IsNullOrWhiteSpace(result.MatchedRuleId));
    }

    [Theory]
    [InlineData("Implemented the requested changes and all tests pass.")]
    [InlineData("Could this be improved later?")]
    [InlineData("You can consider another option in the future.")]
    [InlineData("完成实现。是否还有别的建议？")]
    [InlineData("确认逻辑已经通过测试。")]
    public void Classify_NormalCompletionOrQuestion_DoesNotMisclassify(string message)
    {
        var result = new CodexActionRequestClassifier().Classify(message);

        Assert.Equal(AgentActionTypes.None, result.ActionType);
        Assert.False(result.IsActionRequired);
        Assert.Null(result.MatchedRuleId);
    }

    [Fact]
    public void Transform_ActionRequest_DoesNotRetainSourceText()
    {
        const string Message = "Please confirm before I continue with private details.";
        var transformed = new CodexEventTransformer().Transform(new CodexStopHookPayload
        {
            HookEventName = "Stop",
            SessionId = "session",
            TurnId = "turn",
            LastAssistantMessage = Message,
        });

        Assert.Equal(AgentEventCategories.ActionRequired, transformed.Category);
        Assert.Equal(AgentActionTypes.ConfirmationRequired, transformed.ActionType);
        Assert.Null(transformed.Summary);
        Assert.DoesNotContain(Message, System.Text.Json.JsonSerializer.Serialize(transformed));
    }

    [Fact]
    public void Transform_DetectionDisabled_FallsBackToCompletion()
    {
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            DetectQuestionsInCompletedResponses = false,
        });
        var transformer = new CodexEventTransformer(settings: settings);

        var transformed = transformer.Transform(new CodexStopHookPayload
        {
            HookEventName = "Stop",
            LastAssistantMessage = "Please confirm before I continue.",
        });

        Assert.Equal(AgentEventCategories.Completion, transformed.Category);
        Assert.Equal(AgentActionTypes.None, transformed.ActionType);
    }
}
