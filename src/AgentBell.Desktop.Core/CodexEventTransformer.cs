using System.Globalization;
using System.Text;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Converts a validated Codex Stop payload into sanitized local event data.</summary>
public sealed class CodexEventTransformer
{
    /// <summary>The maximum number of Unicode text elements retained in a summary.</summary>
    public const int MaxSummaryTextElements = 160;

    private readonly TimeProvider _timeProvider;
    private readonly CodexActionRequestClassifier _classifier;
    private readonly DesktopNotificationSettingsState _settings;

    /// <summary>Initializes a transformer using the system clock unless another clock is supplied.</summary>
    public CodexEventTransformer(
        TimeProvider? timeProvider = null,
        CodexActionRequestClassifier? classifier = null,
        DesktopNotificationSettingsState? settings = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classifier = classifier ?? new CodexActionRequestClassifier();
        _settings = settings ?? new DesktopNotificationSettingsState();
    }

    /// <summary>Creates a sanitized event candidate. The pipeline assigns its final sequence.</summary>
    public AgentEvent Transform(CodexStopHookPayload payload)
        => TransformWithClassification(payload).Event;

    /// <summary>Creates a sanitized event and content-free classifier metadata.</summary>
    public ClassifiedAgentEvent TransformWithClassification(CodexStopHookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var threadIdHash = HashIdentifier(payload.SessionId);
        var turnIdHash = HashIdentifier(payload.TurnId);
        var classification = _settings.Current.DetectQuestionsInCompletedResponses
            ? _classifier.Classify(payload.LastAssistantMessage)
            : ActionClassification.Completed;
        var isActionRequired = classification.IsActionRequired;

        return new ClassifiedAgentEvent(
            new AgentEvent
            {
                EventId = CreateEventId(threadIdHash, turnIdHash, classification.ActionType),
                Agent = "codex",
                Status = isActionRequired ? "action_required" : "completed",
                Category = isActionRequired
                ? AgentEventCategories.ActionRequired
                : AgentEventCategories.Completion,
                ActionType = classification.ActionType,
                ToolCategory = AgentToolCategories.None,
                Title = isActionRequired ? "Codex action required" : "Codex turn completed",
                Project = ExtractProject(payload.WorkingDirectory),
                Summary = isActionRequired ? null : CreateSummary(payload.LastAssistantMessage),
                ThreadIdHash = threadIdHash,
                TurnIdHash = turnIdHash,
                OccurredAt = _timeProvider.GetUtcNow(),
                Sequence = 0,
            },
            classification.MatchedRuleId,
            classification.ConfidenceBand);
    }

    /// <summary>Creates an AgentEvent from the content-free PermissionRequest contract.</summary>
    public AgentEvent Transform(SanitizedActionRequiredEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new AgentEvent
        {
            EventId = payload.EventId,
            Agent = "codex",
            Status = "action_required",
            Category = AgentEventCategories.ActionRequired,
            ActionType = AgentActionTypes.PermissionRequired,
            ToolCategory = payload.ToolCategory,
            Title = "Codex action required",
            Project = payload.Project,
            Summary = null,
            ThreadIdHash = payload.SessionIdHash,
            TurnIdHash = payload.TurnIdHash,
            ToolUseIdHash = payload.ToolUseIdHash,
            OccurredAt = payload.OccurredAt,
            Sequence = 0,
        };
    }

    /// <summary>Returns a deterministic 12-character SHA-256 reference without a random salt.</summary>
    public static string? HashIdentifier(string? identifier) => IdentifierHash.Create(identifier);

    /// <summary>Extracts only the final directory segment from an untrusted path-like value.</summary>
    public static string? ExtractProject(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var trimmed = workingDirectory.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return null;
        }

        var separatorIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var segment = separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
            ? null
            : segment;
    }

    /// <summary>Normalizes whitespace and truncates without splitting a Unicode text element.</summary>
    public static string? CreateSummary(string? assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(assistantMessage))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(assistantMessage);
        if (normalized.Length == 0)
        {
            return null;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(normalized);
        var builder = new StringBuilder(Math.Min(normalized.Length, MaxSummaryTextElements * 2));
        var count = 0;
        while (count < MaxSummaryTextElements && enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());
            count++;
        }

        return builder.ToString();
    }

    internal static string CreateEventId(
        string? threadIdHash,
        string? turnIdHash,
        string actionType)
    {
        if (actionType != AgentActionTypes.None)
        {
            return $"codex-action:{IdentifierHash.CreateFingerprint(string.Join(
                '|',
                "codex-stop",
                threadIdHash ?? "missing-session",
                turnIdHash ?? "missing-turn",
                actionType))}";
        }

        if (threadIdHash is not null && turnIdHash is not null)
        {
            return $"codex:{threadIdHash}:{turnIdHash}";
        }

        if (turnIdHash is not null)
        {
            return $"codex:{turnIdHash}";
        }

        return $"codex-local:{Guid.NewGuid():N}";
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var whitespacePending = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>Pairs a sanitized event with classifier metadata that contains no source text.</summary>
public sealed record ClassifiedAgentEvent(
    AgentEvent Event,
    string? MatchedRuleId,
    string ConfidenceBand);
