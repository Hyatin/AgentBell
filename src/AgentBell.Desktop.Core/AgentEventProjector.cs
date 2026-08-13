using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Projects sanitized provider-neutral events onto the existing protocol-v1 event shape.</summary>
public sealed class AgentEventProjector
{
    /// <summary>Creates a deliverable event without changing the existing wire schema.</summary>
    public AgentEvent? Project(
        NormalizedAgentEvent normalizedEvent,
        NotificationDecision notification,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.NotificationKind == NotificationKind.None)
        {
            return null;
        }

        var presentation = notification.PresentationKind
            ?? throw new ArgumentException("A deliverable notification requires a presentation.", nameof(notification));
        var actionType = ToLegacyActionType(presentation);
        var actionRequired = actionType != AgentActionTypes.None;
        var agentDisplayName = ToDisplayName(normalizedEvent.ProviderId.Value);
        return new AgentEvent
        {
            EventId = normalizedEvent.EventId,
            Agent = normalizedEvent.ProviderId.Value,
            Status = actionRequired ? "action_required" : "completed",
            Category = actionRequired
                ? AgentEventCategories.ActionRequired
                : AgentEventCategories.Completion,
            ActionType = actionType,
            ToolCategory = normalizedEvent.ToolCategory,
            Title = actionRequired
                ? $"{agentDisplayName} action required"
                : $"{agentDisplayName} turn completed",
            Project = normalizedEvent.Project,
            Summary = actionRequired ? null : normalizedEvent.SafeSummary,
            ThreadIdHash = normalizedEvent.SessionIdHash,
            TurnIdHash = normalizedEvent.TurnIdHash,
            ToolUseIdHash = normalizedEvent.ToolUseIdHash,
            OccurredAt = normalizedEvent.OccurredAt,
            Sequence = sequence,
        };
    }

    /// <summary>Reconstructs only the existing persisted lifecycle-bearing wire representation.</summary>
    internal bool TryCreateRestoredLifecycle(
        AgentEvent agentEvent,
        out EventLifecycleRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        registration = null;
        if (agentEvent.ActionType != AgentActionTypes.PermissionRequired)
        {
            return false;
        }

        try
        {
            registration = new EventLifecycleRegistration(
                new ProviderId(agentEvent.Agent),
                agentEvent.EventId,
                agentEvent.ThreadIdHash,
                agentEvent.TurnIdHash,
                agentEvent.ToolUseIdHash,
                agentEvent.ToolCategory,
                agentEvent.Project,
                agentEvent.OccurredAt,
                agentEvent.ResolvedAt is null
                    ? EventLifecycleState.Delivered
                    : EventLifecycleState.Resolved,
                agentEvent);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string ToLegacyActionType(PresentationKind presentation) => presentation switch
    {
        PresentationKind.Completion => AgentActionTypes.None,
        PresentationKind.PermissionObserved or PresentationKind.PermissionRequired =>
            AgentActionTypes.PermissionRequired,
        PresentationKind.InputRequired => AgentActionTypes.InputRequired,
        PresentationKind.ConfirmationRequired => AgentActionTypes.ConfirmationRequired,
        PresentationKind.AttentionRequired => AgentActionTypes.AttentionRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(presentation), presentation, null),
    };

    private static string ToDisplayName(string providerId) => string.Join(
        ' ',
        providerId.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

}
