using System.Reflection;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class NotificationDecisionTests
{
    [Theory]
    [MemberData(nameof(ValidDecisions))]
    public void ValidSemanticDecisions_RoundTripDeterministically(NotificationDecision decision)
    {
        var firstJson = JsonSerializer.Serialize(decision);
        var secondJson = JsonSerializer.Serialize(decision);
        var roundTrip = JsonSerializer.Deserialize<NotificationDecision>(firstJson);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(decision, roundTrip);
    }

    [Fact]
    public void SuppressedDecision_HasNoPresentation()
    {
        var decision = new NotificationDecision(
            NotificationKind.None,
            null,
            NotificationReason.PolicySuppressed);

        Assert.Null(decision.PresentationKind);
    }

    [Theory]
    [MemberData(nameof(InvalidDecisions))]
    public void ConflictingSemanticDecision_IsRejected(
        NotificationKind notificationKind,
        PresentationKind? presentationKind,
        NotificationReason reason)
    {
        Assert.Throws<ArgumentException>(() => new NotificationDecision(
            notificationKind,
            presentationKind,
            reason));
    }

    [Fact]
    public void NotificationDecision_HasNoTransportOrDeviceSpecificFields()
    {
        string[] forbidden =
        [
            "DeviceId", "PublishAndroid", "PublishWindows", "AndroidChannel", "WindowsToastId",
            "Transport", "Endpoint", "ShouldPersist",
        ];
        var propertyNames = typeof(NotificationDecision)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => forbidden.Contains(name, StringComparer.Ordinal));
    }

    public static TheoryData<NotificationDecision> ValidDecisions => new()
    {
        new NotificationDecision(NotificationKind.None, null, NotificationReason.PolicySuppressed),
        new NotificationDecision(
            NotificationKind.Completion,
            PresentationKind.Completion,
            NotificationReason.CompletionObserved),
        new NotificationDecision(
            NotificationKind.Observation,
            PresentationKind.PermissionObserved,
            NotificationReason.PermissionOccurrencePolicy),
        new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.PermissionRequired,
            NotificationReason.ConfirmedActionRequired),
        new NotificationDecision(
            NotificationKind.ActionRequired,
            PresentationKind.InputRequired,
            NotificationReason.BestEffortInputDetection),
    };

    public static TheoryData<NotificationKind, PresentationKind?, NotificationReason> InvalidDecisions => new()
    {
        { NotificationKind.None, PresentationKind.Completion, NotificationReason.PolicySuppressed },
        { NotificationKind.None, null, NotificationReason.CompletionObserved },
        { NotificationKind.Completion, null, NotificationReason.CompletionObserved },
        { NotificationKind.Completion, PresentationKind.InputRequired, NotificationReason.CompletionObserved },
        {
            NotificationKind.ActionRequired,
            PresentationKind.PermissionObserved,
            NotificationReason.ConfirmedActionRequired
        },
        {
            NotificationKind.Observation,
            PresentationKind.PermissionRequired,
            NotificationReason.PermissionOccurrencePolicy
        },
        {
            NotificationKind.Observation,
            PresentationKind.PermissionObserved,
            NotificationReason.ConfirmedActionRequired
        },
        {
            NotificationKind.ActionRequired,
            PresentationKind.PermissionRequired,
            NotificationReason.PermissionOccurrencePolicy
        },
        {
            NotificationKind.ActionRequired,
            PresentationKind.InputRequired,
            NotificationReason.BestEffortActionDetection
        },
        {
            NotificationKind.ActionRequired,
            PresentationKind.ConfirmationRequired,
            NotificationReason.BestEffortInputDetection
        },
    };
}
