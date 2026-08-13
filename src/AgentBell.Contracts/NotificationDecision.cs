using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Identifies the semantic notification family selected by future policy evaluation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationKind>))]
public enum NotificationKind
{
    /// <summary>No deliverable event is emitted.</summary>
    None,

    /// <summary>A completion notification is emitted.</summary>
    Completion,

    /// <summary>An observed occurrence is emitted without asserting that user action is required.</summary>
    Observation,

    /// <summary>An action-oriented notification is emitted.</summary>
    ActionRequired,
}

/// <summary>Preserves the precise semantic presentation of a deliverable event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PresentationKind>))]
public enum PresentationKind
{
    /// <summary>Presents a completed turn.</summary>
    Completion,

    /// <summary>Presents an observed permission occurrence without asserting required action.</summary>
    PermissionObserved,

    /// <summary>Presents a permission decision known to be required.</summary>
    PermissionRequired,

    /// <summary>Presents a request for user input.</summary>
    InputRequired,

    /// <summary>Presents a request for confirmation.</summary>
    ConfirmationRequired,

    /// <summary>Presents another condition requiring attention.</summary>
    AttentionRequired,
}

/// <summary>Provides a stable, non-display reason for a semantic notification decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationReason>))]
public enum NotificationReason
{
    /// <summary>Semantic policy suppresses a deliverable event.</summary>
    PolicySuppressed,

    /// <summary>A turn completion was observed.</summary>
    CompletionObserved,

    /// <summary>Policy elects to notify about a permission occurrence.</summary>
    PermissionOccurrencePolicy,

    /// <summary>The normalized event establishes required user action.</summary>
    ConfirmedActionRequired,

    /// <summary>A best-effort classifier detected a request for input.</summary>
    BestEffortInputDetection,

    /// <summary>A best-effort classifier detected another action-oriented condition.</summary>
    BestEffortActionDetection,
}

/// <summary>
/// Expresses semantic delivery intent; device-specific display and transport preferences remain
/// outside this contract.
/// </summary>
public sealed record NotificationDecision
{
    /// <summary>Initializes a semantic notification decision.</summary>
    [JsonConstructor]
    public NotificationDecision(
        NotificationKind notificationKind,
        PresentationKind? presentationKind,
        NotificationReason reason)
    {
        NotificationKind = ContractValueValidation.ValidateEnum(notificationKind, nameof(notificationKind));
        PresentationKind = presentationKind is null
            ? null
            : ContractValueValidation.ValidateEnum(presentationKind.Value, nameof(presentationKind));
        Reason = ContractValueValidation.ValidateEnum(reason, nameof(reason));
        ValidateShape();
    }

    /// <summary>
    /// Gets the semantic notification family. A non-<see cref="NotificationKind.None"/> value
    /// represents a deliverable event; persistence is not a separate device policy flag.
    /// </summary>
    [JsonPropertyName("notificationKind")]
    public NotificationKind NotificationKind { get; }

    /// <summary>Gets the precise presentation meaning, or no presentation when suppressed.</summary>
    [JsonPropertyName("presentationKind")]
    public PresentationKind? PresentationKind { get; }

    /// <summary>Gets the stable reason for the decision.</summary>
    [JsonPropertyName("reason")]
    public NotificationReason Reason { get; }

    private void ValidateShape()
    {
        switch (NotificationKind)
        {
            case NotificationKind.None when PresentationKind is not null
                || Reason != NotificationReason.PolicySuppressed:
                throw new ArgumentException("A suppressed decision must have no presentation and a suppression reason.");
            case NotificationKind.Completion when PresentationKind != Contracts.PresentationKind.Completion
                || Reason != NotificationReason.CompletionObserved:
                throw new ArgumentException("A completion decision has conflicting presentation or reason.");
            case NotificationKind.Observation when
                PresentationKind != Contracts.PresentationKind.PermissionObserved
                || Reason != NotificationReason.PermissionOccurrencePolicy:
                throw new ArgumentException("An observation decision has conflicting presentation or reason.");
            case NotificationKind.ActionRequired when PresentationKind is null
                or Contracts.PresentationKind.Completion
                or Contracts.PresentationKind.PermissionObserved:
                throw new ArgumentException("An action-oriented decision requires a non-completion presentation.");
            case NotificationKind.ActionRequired when
                PresentationKind == Contracts.PresentationKind.PermissionRequired
                && Reason != NotificationReason.ConfirmedActionRequired:
                throw new ArgumentException("A required permission requires a confirmed-action reason.");
            case NotificationKind.ActionRequired when
                PresentationKind == Contracts.PresentationKind.InputRequired
                && Reason is not NotificationReason.ConfirmedActionRequired
                    and not NotificationReason.BestEffortInputDetection:
                throw new ArgumentException("An input request has a conflicting reason.");
            case NotificationKind.ActionRequired when
                PresentationKind is Contracts.PresentationKind.ConfirmationRequired
                    or Contracts.PresentationKind.AttentionRequired
                && Reason is not NotificationReason.ConfirmedActionRequired
                    and not NotificationReason.BestEffortActionDetection:
                throw new ArgumentException("The action-oriented presentation has a conflicting reason.");
            case NotificationKind.None:
            case NotificationKind.Completion:
            case NotificationKind.Observation:
            case NotificationKind.ActionRequired:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(NotificationKind),
                    NotificationKind,
                    "The notification kind is undefined.");
        }
    }
}
