using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class EventLifecycleTrackerTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly string SessionHash = IdentifierHash.Create("session")!;
    private static readonly string TurnHash = IdentifierHash.Create("turn")!;
    private const string ToolHash = "fedcba654321";

    [Fact]
    public async Task RegisterSuppressed_TracksWithoutPersistenceOrPublication()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Pipeline.AcceptAsync(
            Register("codex-action:tracked", ProviderIds.Codex, deliver: false),
            CancellationToken.None);

        Assert.Equal(EventLifecycleState.Tracked, result.LifecycleState);
        Assert.Null(result.Event);
        Assert.Empty(harness.Store.Snapshot);
        Assert.Empty(harness.Publisher.Events);
    }

    [Fact]
    public async Task RegisterDelivered_PersistsAndPublishesExactlyOnce()
    {
        await using var harness = await Harness.CreateAsync();

        var first = await harness.Pipeline.AcceptAsync(
            Register("codex-action:delivered", ProviderIds.Codex, deliver: true),
            CancellationToken.None);
        var duplicate = await harness.Pipeline.AcceptAsync(
            Register("codex-action:delivered", ProviderIds.Codex, deliver: true),
            CancellationToken.None);

        Assert.Equal(EventLifecycleState.Delivered, first.LifecycleState);
        Assert.True(duplicate.IsDuplicate);
        Assert.Single(harness.Store.Snapshot);
        Assert.Single(harness.Publisher.Events);
    }

    [Fact]
    public async Task ResolveOne_ResolvesMatchingDeliveredEntryAndPublishesResolutionUpdate()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Pipeline.AcceptAsync(
            Register("codex-action:resolve-one", ProviderIds.Codex, deliver: true),
            CancellationToken.None);

        var result = await harness.Pipeline.AcceptAsync(
            ResolveOne(ProviderIds.Codex),
            CancellationToken.None);

        Assert.True(result.LifecycleResolution.Matched);
        Assert.Equal(1, result.LifecycleResolution.DeliveredResolved);
        Assert.NotNull(Assert.Single(harness.Store.Snapshot).ResolvedAt);
        Assert.Equal(2, harness.Publisher.Events.Count);
    }

    [Fact]
    public async Task ResolveAllInTurn_ResolvesEveryProviderScopedEntry()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Pipeline.AcceptAsync(
            Register("codex-action:first", ProviderIds.Codex, deliver: false, toolHash: null),
            CancellationToken.None);
        await harness.Pipeline.AcceptAsync(
            Register("codex-action:second", ProviderIds.Codex, deliver: false, toolHash: "111111111111"),
            CancellationToken.None);

        var result = await harness.Pipeline.AcceptAsync(
            ResolveAll(ProviderIds.Codex),
            CancellationToken.None);

        Assert.Equal(2, result.LifecycleResolution.TrackedResolved);
    }

    [Fact]
    public async Task CompatibilityStop_ResolvesDeliveredEntryThenEmitsCompletion()
    {
        await using var harness = await Harness.CreateAsync();
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            PermissionNotificationPolicy = PermissionNotificationPolicy.AlwaysNotify,
        });
        var factory = new CodexPipelineSubmissionFactory(
            new ManualTimeProvider(FixedTime),
            settings: settings);
        await harness.Pipeline.AcceptAsync(
            factory.Create(new SanitizedActionRequiredEvent
            {
                EventId = "codex-action:stop-lifecycle",
                SessionIdHash = SessionHash,
                TurnIdHash = TurnHash,
                ToolUseIdHash = ToolHash,
                Project = "AgentBell",
                ToolCategory = AgentToolCategories.Command,
                OccurredAt = FixedTime,
            }),
            CancellationToken.None);

        var completion = await harness.Pipeline.AcceptAsync(
            factory.Create(new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "session",
                TurnId = "turn",
                LastAssistantMessage = "Completed normally.",
            }),
            CancellationToken.None);

        Assert.Equal(1, completion.LifecycleResolution.DeliveredResolved);
        Assert.Equal(AgentEventCategories.Completion, completion.Event?.Category);
        Assert.Contains(harness.Publisher.Events, item => item.ResolvedAt is not null);
        Assert.Contains(harness.Publisher.Events, item => item.Category == AgentEventCategories.Completion);
    }

    [Fact]
    public async Task PersistedPayloadNeverContainsRawOrUnboundedContent()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Pipeline.AcceptAsync(
            Register("codex-action:private-safe", ProviderIds.Codex, deliver: true),
            CancellationToken.None);

        var serialized = JsonSerializer.Serialize(harness.Store.Snapshot);

        Assert.DoesNotContain("tool_input", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-session", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-turn", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameCorrelationHashes_DifferentProviderCannotCrossResolve()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Pipeline.AcceptAsync(
            Register("codex-action:provider-scope", ProviderIds.Codex, deliver: false),
            CancellationToken.None);
        await harness.Pipeline.AcceptAsync(
            Register("claude-code:provider-scope", ProviderIds.ClaudeCode, deliver: false),
            CancellationToken.None);

        var claudeResolution = await harness.Pipeline.AcceptAsync(
            ResolveOne(ProviderIds.ClaudeCode),
            CancellationToken.None);

        Assert.Equal(1, claudeResolution.LifecycleResolution.TrackedResolved);
        Assert.Equal(
            EventLifecycleState.Tracked,
            (await harness.Pipeline.GetLifecycleAsync(
                ProviderIds.Codex,
                "codex-action:provider-scope",
                CancellationToken.None))?.State);
        Assert.Equal(
            EventLifecycleState.Resolved,
            (await harness.Pipeline.GetLifecycleAsync(
                ProviderIds.ClaudeCode,
                "claude-code:provider-scope",
                CancellationToken.None))?.State);
    }

    [Fact]
    public void SameEventIdAndCorrelation_DifferentProviderRetainsIndependentLifecycleState()
    {
        var tracker = new EventLifecycleTracker(EventPipeline.DeduplicationCapacity);
        const string sharedEventId = "shared:event-000000000001";
        tracker.Register(Registration(sharedEventId, ProviderIds.Codex));
        tracker.Register(Registration(sharedEventId, ProviderIds.ClaudeCode));

        var claudeResolution = tracker.Resolve(
            ProviderIds.ClaudeCode,
            ResolveOne(ProviderIds.ClaudeCode).Lifecycle);

        Assert.Single(claudeResolution);
        Assert.Equal(
            EventLifecycleState.Tracked,
            tracker.Get(ProviderIds.Codex, sharedEventId)?.State);
        Assert.Equal(
            EventLifecycleState.Resolved,
            tracker.Get(ProviderIds.ClaudeCode, sharedEventId)?.State);
    }

    [Fact]
    public async Task ConcurrentProviders_DoNotCrossResolveOrDuplicateSequence()
    {
        await using var harness = await Harness.CreateAsync();
        var registrations = Enumerable.Range(0, 20)
            .SelectMany(index => new[]
            {
                Register($"codex-action:event-{index}", ProviderIds.Codex, deliver: true),
                Register($"claude-code:event-{index}", ProviderIds.ClaudeCode, deliver: true),
            });

        var results = await Task.WhenAll(registrations.Select(submission =>
            harness.Pipeline.AcceptAsync(submission, CancellationToken.None)));

        Assert.Equal(40, results.Select(result => result.Event!.Sequence).Distinct().Count());
        var codexResolution = await harness.Pipeline.AcceptAsync(
            ResolveAll(ProviderIds.Codex),
            CancellationToken.None);
        Assert.Equal(20, codexResolution.LifecycleResolution.DeliveredResolved);
        Assert.Equal(
            EventLifecycleState.Delivered,
            (await harness.Pipeline.GetLifecycleAsync(
                ProviderIds.ClaudeCode,
                "claude-code:event-0",
                CancellationToken.None))?.State);
    }

    [Fact]
    public async Task LifecycleCache_HasA1000ItemBound()
    {
        await using var harness = await Harness.CreateAsync();
        for (var index = 0; index <= EventPipeline.DeduplicationCapacity; index++)
        {
            await harness.Pipeline.AcceptAsync(
                Register($"codex-action:{index:x24}", ProviderIds.Codex, deliver: false),
                CancellationToken.None);
        }

        Assert.Null(await harness.Pipeline.GetLifecycleAsync(
            ProviderIds.Codex,
            "codex-action:000000000000000000000000",
            CancellationToken.None));
        Assert.NotNull(await harness.Pipeline.GetLifecycleAsync(
            ProviderIds.Codex,
            $"codex-action:{EventPipeline.DeduplicationCapacity:x24}",
            CancellationToken.None));
    }

    private static EventPipelineSubmission Register(
        string eventId,
        ProviderId providerId,
        bool deliver,
        string? toolHash = ToolHash)
    {
        var normalized = new NormalizedAgentEvent(
            providerId,
            new SourceEventKind("permission-request"),
            SemanticEventKind.PermissionObserved,
            FixedTime,
            eventId,
            SessionHash,
            TurnHash,
            toolHash,
            "AgentBell",
            null,
            AgentToolCategories.Command,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.Unknown);
        return new EventPipelineSubmission(
            providerId,
            normalized,
            deliver
                ? new NotificationDecision(
                    NotificationKind.Observation,
                    PresentationKind.PermissionObserved,
                    NotificationReason.PermissionOccurrencePolicy)
                : EventPipelineTests.Suppressed(),
            new LifecycleDirective(
                LifecycleDirectiveKind.Register,
                eventId,
                SessionHash,
                TurnHash,
                toolHash,
                AgentToolCategories.Command));
    }

    private static EventPipelineSubmission ResolveOne(ProviderId providerId) => new(
        providerId,
        null,
        EventPipelineTests.Suppressed(),
        new LifecycleDirective(
            LifecycleDirectiveKind.ResolveOne,
            null,
            SessionHash,
            TurnHash,
            ToolHash,
            AgentToolCategories.Command));

    private static EventPipelineSubmission ResolveAll(ProviderId providerId) => new(
        providerId,
        null,
        EventPipelineTests.Suppressed(),
        new LifecycleDirective(
            LifecycleDirectiveKind.ResolveAllInTurn,
            null,
            SessionHash,
            TurnHash,
            null,
            null));

    private static EventLifecycleRegistration Registration(string eventId, ProviderId providerId) =>
        new(
            providerId,
            eventId,
            SessionHash,
            TurnHash,
            ToolHash,
            AgentToolCategories.Command,
            "AgentBell",
            FixedTime,
            EventLifecycleState.Tracked,
            null);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            InMemoryEventStore store,
            CollectingEventPublisher publisher,
            EventPipeline pipeline)
        {
            Store = store;
            Publisher = publisher;
            Pipeline = pipeline;
        }

        public InMemoryEventStore Store { get; }

        public CollectingEventPublisher Publisher { get; }

        public EventPipeline Pipeline { get; }

        public static async Task<Harness> CreateAsync()
        {
            var store = new InMemoryEventStore();
            var publisher = new CollectingEventPublisher();
            var pipeline = new EventPipeline(
                store,
                eventPublisher: publisher,
                timeProvider: new ManualTimeProvider(FixedTime));
            await pipeline.InitializeAsync(CancellationToken.None);
            return new Harness(store, publisher, pipeline);
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }
}
