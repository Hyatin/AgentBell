using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Contains one sanitized provider-neutral pipeline operation.</summary>
public sealed record EventPipelineSubmission
{
    /// <summary>Initializes a bounded event and lifecycle submission.</summary>
    public EventPipelineSubmission(
        ProviderId providerId,
        NormalizedAgentEvent? normalizedEvent,
        NotificationDecision notification,
        LifecycleDirective lifecycle)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _ = providerId.Value;
        if (normalizedEvent is not null && normalizedEvent.ProviderId != providerId)
        {
            throw new ArgumentException("The event provider must match the submission provider.", nameof(providerId));
        }

        if (notification.NotificationKind != NotificationKind.None && normalizedEvent is null)
        {
            throw new ArgumentException("A deliverable submission requires a normalized event.", nameof(normalizedEvent));
        }

        if (normalizedEvent is not null)
        {
            ValidateSemanticDecision(normalizedEvent, notification);
        }

        if (lifecycle.Kind == LifecycleDirectiveKind.Register
            && (normalizedEvent is null || lifecycle.EventId != normalizedEvent.EventId))
        {
            throw new ArgumentException(
                "A lifecycle registration must identify the normalized event.",
                nameof(lifecycle));
        }

        ProviderId = providerId;
        NormalizedEvent = normalizedEvent;
        Notification = notification;
        Lifecycle = lifecycle;
    }

    /// <summary>Gets the provider namespace used for lifecycle correlation.</summary>
    public ProviderId ProviderId { get; }

    /// <summary>Gets the sanitized event, or null for a lifecycle-only operation.</summary>
    public NormalizedAgentEvent? NormalizedEvent { get; }

    /// <summary>Gets the already-evaluated semantic notification decision.</summary>
    public NotificationDecision Notification { get; }

    /// <summary>Gets the bounded lifecycle operation.</summary>
    public LifecycleDirective Lifecycle { get; }

    private static void ValidateSemanticDecision(
        NormalizedAgentEvent normalizedEvent,
        NotificationDecision notification)
    {
        if (notification.NotificationKind == NotificationKind.None)
        {
            return;
        }

        var valid = (normalizedEvent.SemanticEventKind, notification.PresentationKind) switch
        {
            (SemanticEventKind.TurnCompleted, PresentationKind.Completion) => true,
            (SemanticEventKind.PermissionObserved, PresentationKind.PermissionObserved) => true,
            (SemanticEventKind.PermissionRequired, PresentationKind.PermissionRequired) => true,
            (SemanticEventKind.InputRequired, PresentationKind.InputRequired) => true,
            (SemanticEventKind.ConfirmationRequired, PresentationKind.ConfirmationRequired) => true,
            (SemanticEventKind.AttentionRequired, PresentationKind.AttentionRequired) => true,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The notification presentation conflicts with the normalized event meaning.",
                nameof(notification));
        }
    }
}
