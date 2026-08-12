using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Classifies only explicit high-confidence action requests in a Stop response.</summary>
public sealed class CodexActionRequestClassifier
{
    private static readonly ClassificationRule[] Rules =
    [
        // Priority: permission > input > confirmation > attention > completion.
        new(AgentActionTypes.PermissionRequired, "permission_en_approval", "i need your approval"),
        new(AgentActionTypes.PermissionRequired, "permission_en_required", "approval is required"),
        new(AgentActionTypes.PermissionRequired, "permission_en_permission", "i need your permission"),
        new(AgentActionTypes.PermissionRequired, "permission_zh_approval", "需要你的批准"),
        new(AgentActionTypes.PermissionRequired, "permission_zh_permission", "需要你的权限确认"),

        new(AgentActionTypes.InputRequired, "input_en_choose", "choose one of the following"),
        new(AgentActionTypes.InputRequired, "input_en_option", "which option should i use"),
        new(AgentActionTypes.InputRequired, "input_en_provide", "please provide"),
        new(AgentActionTypes.InputRequired, "input_en_select", "please select"),
        new(AgentActionTypes.InputRequired, "input_en_information", "i need the following information"),
        new(AgentActionTypes.InputRequired, "input_en_reply", "reply with"),
        new(AgentActionTypes.InputRequired, "input_zh_select", "请选择"),
        new(AgentActionTypes.InputRequired, "input_zh_provide", "请提供"),
        new(AgentActionTypes.InputRequired, "input_zh_reply", "请回复"),
        new(AgentActionTypes.InputRequired, "input_zh_information", "需要以下信息"),
        new(AgentActionTypes.InputRequired, "input_zh_option", "你希望选择哪一个"),

        new(AgentActionTypes.ConfirmationRequired, "confirm_en_please", "please confirm"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_en_before", "confirm before i continue"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_en_proceed", "should i proceed"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_en_would_proceed", "would you like me to proceed"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_zh_please", "请确认"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_zh_before", "确认后我再继续"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_zh_needed", "需要你的确认"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_zh_continue", "是否继续"),
        new(AgentActionTypes.ConfirmationRequired, "confirm_zh_can_continue", "我可以继续吗"),

        new(AgentActionTypes.AttentionRequired, "attention_en_blocked", "i cannot continue until"),
        new(AgentActionTypes.AttentionRequired, "attention_en_attention", "i need your attention"),
        new(AgentActionTypes.AttentionRequired, "attention_zh_blocked", "在你确认之前无法继续"),
        new(AgentActionTypes.AttentionRequired, "attention_zh_handle", "需要你处理"),
    ];

    /// <summary>Returns completion unless an explicit multi-word rule matches.</summary>
    public ActionClassification Classify(string? assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(assistantMessage))
        {
            return ActionClassification.Completed;
        }

        foreach (var rule in Rules)
        {
            if (assistantMessage.Contains(rule.Phrase, StringComparison.OrdinalIgnoreCase))
            {
                return new ActionClassification(rule.ActionType, rule.RuleId, "high");
            }
        }

        return ActionClassification.Completed;
    }

    private sealed record ClassificationRule(string ActionType, string RuleId, string Phrase);
}

/// <summary>Contains no source text, only the classifier decision and rule metadata.</summary>
public sealed record ActionClassification(
    string ActionType,
    string? MatchedRuleId,
    string ConfidenceBand)
{
    /// <summary>The ordinary completion fallback.</summary>
    public static ActionClassification Completed { get; } =
        new(AgentActionTypes.None, null, "none");

    /// <summary>Gets whether the decision requires user action.</summary>
    public bool IsActionRequired => ActionType != AgentActionTypes.None;
}
