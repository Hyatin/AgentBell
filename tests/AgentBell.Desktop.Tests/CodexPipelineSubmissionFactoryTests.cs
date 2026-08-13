using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class CodexPipelineSubmissionFactoryTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 7, 12, 34, 56, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "Synthetic work completed.",
        SemanticEventKind.TurnCompleted,
        NotificationKind.Completion,
        PresentationKind.Completion,
        "codex:e18c78136e8e:04d186e38f9a")]
    [InlineData(
        "Please provide the synthetic choice.",
        SemanticEventKind.InputRequired,
        NotificationKind.ActionRequired,
        PresentationKind.InputRequired,
        "codex-action:b6d7891a91c10840802e2b6c")]
    [InlineData(
        "Please confirm before I continue.",
        SemanticEventKind.ConfirmationRequired,
        NotificationKind.ActionRequired,
        PresentationKind.ConfirmationRequired,
        "codex-action:ce06fb39f016f59e85fe225a")]
    [InlineData(
        "I cannot continue until the synthetic fixture is supplied.",
        SemanticEventKind.AttentionRequired,
        NotificationKind.ActionRequired,
        PresentationKind.AttentionRequired,
        "codex-action:9e7e29b6848adae754f5c3c5")]
    public void Stop_MapsToProviderNeutralSubmissionWith07EventId(
        string message,
        SemanticEventKind semanticKind,
        NotificationKind notificationKind,
        PresentationKind presentationKind,
        string eventId)
    {
        var submission = CreateFactory().Create(new CodexStopHookPayload
        {
            HookEventName = "Stop",
            SessionId = "stable-session",
            TurnId = "stable-turn",
            WorkingDirectory = "C:\\Synthetic Parent\\AgentBell",
            LastAssistantMessage = message,
        });

        Assert.Equal(ProviderIds.Codex, submission.ProviderId);
        Assert.Equal(semanticKind, submission.NormalizedEvent?.SemanticEventKind);
        Assert.Equal(eventId, submission.NormalizedEvent?.EventId);
        Assert.Equal(notificationKind, submission.Notification.NotificationKind);
        Assert.Equal(presentationKind, submission.Notification.PresentationKind);
        Assert.Equal(LifecycleDirectiveKind.ResolveAllInTurn, submission.Lifecycle.Kind);
    }

    [Fact]
    public void PermissionOff_MapsObservedSemanticsToSuppressedRegisteredLifecycle()
    {
        var submission = CreateFactory(PermissionNotificationPolicy.Off).Create(Permission());

        Assert.Equal(SemanticEventKind.PermissionObserved, submission.NormalizedEvent?.SemanticEventKind);
        Assert.Equal(ActionRequirement.Unknown, submission.NormalizedEvent?.ActionRequirement);
        Assert.Equal(NotificationKind.None, submission.Notification.NotificationKind);
        Assert.Equal(LifecycleDirectiveKind.Register, submission.Lifecycle.Kind);
    }

    [Fact]
    public void PermissionAlwaysNotify_MapsObservationWithoutClaimingRequiredAction()
    {
        var submission = CreateFactory(PermissionNotificationPolicy.AlwaysNotify).Create(Permission());

        Assert.Equal(SemanticEventKind.PermissionObserved, submission.NormalizedEvent?.SemanticEventKind);
        Assert.Equal(ActionRequirement.Unknown, submission.NormalizedEvent?.ActionRequirement);
        Assert.Equal(NotificationKind.Observation, submission.Notification.NotificationKind);
        Assert.Equal(PresentationKind.PermissionObserved, submission.Notification.PresentationKind);
        Assert.Equal("codex-action:fa4afc83bf185000bb870ddb", submission.NormalizedEvent?.EventId);
    }

    [Fact]
    public void PostToolUse_MapsToLifecycleOnlyResolveOne()
    {
        var submission = CreateFactory().Create(new SanitizedPostToolUseEvent
        {
            SessionIdHash = "e18c78136e8e",
            TurnIdHash = "04d186e38f9a",
            ToolUseIdHash = "abcdef123456",
            ToolCategory = AgentToolCategories.Command,
            OccurredAt = FixedTime,
        });

        Assert.Null(submission.NormalizedEvent);
        Assert.Equal(NotificationKind.None, submission.Notification.NotificationKind);
        Assert.Equal(LifecycleDirectiveKind.ResolveOne, submission.Lifecycle.Kind);
    }

    [Fact]
    public async Task NewPath_IsExactlyEquivalentToLocked07AgentEvents()
    {
        var settings = Settings(PermissionNotificationPolicy.AlwaysNotify);
        var factory = CreateFactory(settings: settings);
        await using var pipeline = new EventPipeline(new InMemoryEventStore());
        await pipeline.InitializeAsync(CancellationToken.None);
        var stopVectors = new[]
        {
            new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                WorkingDirectory = "C:\\Synthetic Parent\\AgentBell",
                LastAssistantMessage = "Synthetic work completed.",
            },
            new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "stable-session",
                TurnId = "new-turn",
                LastAssistantMessage = "Please provide the synthetic choice.",
            },
            new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "stable-session",
                TurnId = "confirm-turn",
                LastAssistantMessage = "Please confirm before I continue.",
            },
            new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "stable-session",
                TurnId = "attention-turn",
                LastAssistantMessage = "I cannot continue until the synthetic fixture is supplied.",
            },
        };
        var transformer = new CodexEventTransformer(
            new ManualTimeProvider(FixedTime),
            settings: settings);

        foreach (var payload in stopVectors)
        {
            var expected = transformer.Transform(payload);
            var actual = Assert.IsType<AgentEvent>((await pipeline.AcceptAsync(
                factory.Create(payload),
                CancellationToken.None)).Event);
            AssertEquivalent(expected, actual, actual.Sequence);
        }

        var permission = Permission();
        var expectedPermission = transformer.Transform(permission);
        var actualPermission = Assert.IsType<AgentEvent>((await pipeline.AcceptAsync(
            factory.Create(permission),
            CancellationToken.None)).Event);
        AssertEquivalent(expectedPermission, actualPermission, actualPermission.Sequence);
    }

    private static void AssertEquivalent(AgentEvent expected, AgentEvent actual, long sequence)
    {
        Assert.Equal(expected with { Sequence = sequence }, actual);
    }

    private static SanitizedActionRequiredEvent Permission() => new()
    {
        EventId = "codex-action:fa4afc83bf185000bb870ddb",
        SessionIdHash = "e18c78136e8e",
        TurnIdHash = "04d186e38f9a",
        ToolUseIdHash = "abcdef123456",
        Project = "AgentBell",
        ToolCategory = AgentToolCategories.Command,
        OccurredAt = FixedTime,
    };

    private static CodexPipelineSubmissionFactory CreateFactory(
        PermissionNotificationPolicy policy = PermissionNotificationPolicy.Off,
        DesktopNotificationSettingsState? settings = null) => new(
            new ManualTimeProvider(FixedTime),
            settings: settings ?? Settings(policy));

    private static DesktopNotificationSettingsState Settings(PermissionNotificationPolicy policy)
    {
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            PermissionNotificationPolicy = policy,
        });
        return settings;
    }
}
