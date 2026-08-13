using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>
/// Bridges the current Desktop-side Codex compatibility contracts to the provider-neutral pipeline.
/// Hook-side provider adaptation remains a later milestone.
/// </summary>
public sealed class CodexPipelineSubmissionFactory
{
    private static readonly NotificationDecision Suppressed = new(
        NotificationKind.None,
        null,
        NotificationReason.PolicySuppressed);

    private readonly TimeProvider _timeProvider;
    private readonly CodexActionRequestClassifier _classifier;
    private readonly DesktopNotificationSettingsState _settings;

    /// <summary>Initializes the temporary Desktop compatibility mapper.</summary>
    public CodexPipelineSubmissionFactory(
        TimeProvider? timeProvider = null,
        CodexActionRequestClassifier? classifier = null,
        DesktopNotificationSettingsState? settings = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classifier = classifier ?? new CodexActionRequestClassifier();
        _settings = settings ?? new DesktopNotificationSettingsState();
    }

    /// <summary>Maps a current Stop payload without changing its deterministic 0.7 identifiers.</summary>
    public EventPipelineSubmission Create(CodexStopHookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var settings = _settings.Current;
        var classification = settings.DetectQuestionsInCompletedResponses
            ? _classifier.Classify(payload.LastAssistantMessage)
            : ActionClassification.Completed;
        var sessionHash = IdentifierHash.Create(payload.SessionId);
        var turnHash = IdentifierHash.Create(payload.TurnId);
        var semanticKind = ToSemanticEventKind(classification.ActionType);
        var actionRequirement = classification.IsActionRequired
            ? ActionRequirement.Required
            : ActionRequirement.None;
        var normalized = new NormalizedAgentEvent(
            ProviderIds.Codex,
            new SourceEventKind("stop"),
            semanticKind,
            _timeProvider.GetUtcNow(),
            CodexEventTransformer.CreateEventId(sessionHash, turnHash, classification.ActionType),
            sessionHash,
            turnHash,
            null,
            CodexEventTransformer.ExtractProject(payload.WorkingDirectory),
            classification.IsActionRequired
                ? null
                : CodexEventTransformer.CreateSummary(payload.LastAssistantMessage),
            AgentToolCategories.None,
            CreateClassification(classification),
            classification.IsActionRequired
                ? SemanticReliability.BestEffort
                : SemanticReliability.Reliable,
            actionRequirement);
        var decision = CreateStopDecision(classification.ActionType);
        var lifecycle = turnHash is null
            ? new LifecycleDirective(LifecycleDirectiveKind.None, null, null, null, null, null)
            : new LifecycleDirective(
                LifecycleDirectiveKind.ResolveAllInTurn,
                null,
                sessionHash,
                turnHash,
                null,
                null);
        return new EventPipelineSubmission(ProviderIds.Codex, normalized, decision, lifecycle);
    }

    /// <summary>Maps the current content-free permission occurrence and evaluates current policy.</summary>
    public EventPipelineSubmission Create(SanitizedActionRequiredEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var normalized = new NormalizedAgentEvent(
            ProviderIds.Codex,
            new SourceEventKind("permission-request"),
            SemanticEventKind.PermissionObserved,
            payload.OccurredAt,
            payload.EventId,
            payload.SessionIdHash,
            payload.TurnIdHash,
            payload.ToolUseIdHash,
            payload.Project,
            null,
            payload.ToolCategory,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.Unknown);
        var decision = _settings.Current.PermissionNotificationPolicy ==
            PermissionNotificationPolicy.AlwaysNotify
                ? new NotificationDecision(
                    NotificationKind.Observation,
                    PresentationKind.PermissionObserved,
                    NotificationReason.PermissionOccurrencePolicy)
                : Suppressed;
        var lifecycle = new LifecycleDirective(
            LifecycleDirectiveKind.Register,
            payload.EventId,
            payload.SessionIdHash,
            payload.TurnIdHash,
            payload.ToolUseIdHash,
            payload.ToolCategory);
        return new EventPipelineSubmission(ProviderIds.Codex, normalized, decision, lifecycle);
    }

    /// <summary>Maps a current content-free lifecycle observation without producing an event.</summary>
    public EventPipelineSubmission Create(SanitizedPostToolUseEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var lifecycle = payload.TurnIdHash is null
            ? new LifecycleDirective(LifecycleDirectiveKind.None, null, null, null, null, null)
            : new LifecycleDirective(
                LifecycleDirectiveKind.ResolveOne,
                null,
                payload.SessionIdHash,
                payload.TurnIdHash,
                payload.ToolUseIdHash,
                payload.ToolCategory);
        return new EventPipelineSubmission(ProviderIds.Codex, null, Suppressed, lifecycle);
    }

    private static EventClassification? CreateClassification(ActionClassification classification) =>
        classification.MatchedRuleId is null
            ? null
            : new EventClassification(
                classification.MatchedRuleId,
                classification.ConfidenceBand == "high"
                    ? ClassificationConfidence.High
                    : ClassificationConfidence.Medium);

    private static SemanticEventKind ToSemanticEventKind(string actionType) => actionType switch
    {
        AgentActionTypes.None => SemanticEventKind.TurnCompleted,
        AgentActionTypes.PermissionRequired => SemanticEventKind.PermissionRequired,
        AgentActionTypes.InputRequired => SemanticEventKind.InputRequired,
        AgentActionTypes.ConfirmationRequired => SemanticEventKind.ConfirmationRequired,
        AgentActionTypes.AttentionRequired => SemanticEventKind.AttentionRequired,
        _ => throw new ArgumentException("The action classification is unsupported.", nameof(actionType)),
    };

    private static NotificationDecision CreateStopDecision(string actionType) => actionType switch
    {
        AgentActionTypes.None => new NotificationDecision(
            NotificationKind.Completion,
            PresentationKind.Completion,
            NotificationReason.CompletionObserved),
        AgentActionTypes.PermissionRequired => new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.PermissionRequired,
            NotificationReason.ConfirmedActionRequired),
        AgentActionTypes.InputRequired => new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.InputRequired,
            NotificationReason.BestEffortInputDetection),
        AgentActionTypes.ConfirmationRequired => new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.ConfirmationRequired,
            NotificationReason.BestEffortActionDetection),
        AgentActionTypes.AttentionRequired => new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.AttentionRequired,
            NotificationReason.BestEffortActionDetection),
        _ => throw new ArgumentException("The action classification is unsupported.", nameof(actionType)),
    };
}
