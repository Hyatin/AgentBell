using System.Collections.Concurrent;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class PermissionNotificationLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly string SessionHash = IdentifierHash.Create("session")!;
    private static readonly string TurnHash = IdentifierHash.Create("turn")!;

    [Fact]
    public async Task DefaultOff_ObservesWithoutHistoryPersistenceOrPublication()
    {
        await using var harness = await Harness.CreateAsync();

        var accepted = await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        Assert.Equal(PermissionLifecycleState.Observed, accepted.PermissionState);
        Assert.Empty(harness.Publisher.Events);
        Assert.Empty(harness.Store.Events);
        Assert.Empty((await harness.Pipeline.GetHistoryAsync(0, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task Off_PostToolUseCorrelatesLifecycleWithoutPublishingStandaloneEvent()
    {
        await using var harness = await Harness.CreateAsync();
        var accepted = await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        var resolved = await harness.Pipeline.ResolveAsync(PostToolUse(), CancellationToken.None);

        Assert.True(resolved.Matched);
        Assert.Equal(1, resolved.ObservedResolved);
        Assert.Equal(PermissionLifecycleState.Resolved, (await harness.Pipeline.GetPermissionAsync(
            accepted.Event.EventId,
            CancellationToken.None))?.State);
        Assert.Empty(harness.Publisher.Events);
        Assert.Empty(harness.Store.Events);
    }

    [Fact]
    public async Task AlwaysNotify_PublishesUniquePermissionImmediatelyExactlyOnce()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify);

        var accepted = await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        Assert.Equal(PermissionLifecycleState.Published, accepted.PermissionState);
        var published = Assert.Single(harness.Publisher.Events);
        Assert.Equal(AgentActionTypes.PermissionRequired, published.ActionType);
        Assert.Equal(AgentEventCategories.ActionRequired, published.Category);
        Assert.Null(published.ResolvedAt);
        Assert.Single(harness.Store.Events);
    }

    [Fact]
    public async Task AlwaysNotify_DuplicatePermissionPublishesAndPersistsOnce()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify);

        var first = await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);
        var duplicate = await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Single(harness.Publisher.Events);
        Assert.Single(harness.Store.Events);
    }

    [Fact]
    public async Task AlwaysNotify_IsIndependentOfGlobalActionDisplaySetting()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify,
            notifyActionRequired: false);

        await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        Assert.Single(harness.Publisher.Events);
        Assert.Single(harness.Store.Events);
    }

    [Fact]
    public async Task PublishedPermission_PostToolUsePersistsResolutionWithoutSecondAlertEvent()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify);
        await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        var resolution = await harness.Pipeline.ResolveAsync(PostToolUse(), CancellationToken.None);

        Assert.Equal(1, resolution.PublishedResolved);
        var persisted = Assert.Single(harness.Store.Events);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.Equal(2, harness.Publisher.Events.Count);
        Assert.NotNull(harness.Publisher.Events.Last().ResolvedAt);
    }

    [Fact]
    public async Task StopBehaviorRemainsAndResolvesAnyPublishedPermission()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify);
        await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        var completion = await harness.Pipeline.AcceptAsync(Stop(), CancellationToken.None);

        Assert.Equal(AgentEventCategories.Completion, completion.Event.Category);
        Assert.Contains(harness.Publisher.Events, item => item.ResolvedAt is not null);
        Assert.Contains(
            harness.Publisher.Events,
            item => item.Category == AgentEventCategories.Completion);
        Assert.DoesNotContain(
            harness.Store.Events,
            item => item.ActionType == AgentActionTypes.PermissionRequired
                && item.ResolvedAt is null);
    }

    [Fact]
    public async Task LifecycleCache_HasA1000ItemBound()
    {
        await using var harness = await Harness.CreateAsync();
        for (var index = 0; index <= EventPipeline.DeduplicationCapacity; index++)
        {
            await harness.Pipeline.AcceptAsync(
                Permission(
                    $"{index:x12}",
                    $"codex-action:{index:x24}"),
                CancellationToken.None);
        }

        Assert.Null(await harness.Pipeline.GetPermissionAsync(
            "codex-action:000000000000000000000000",
            CancellationToken.None));
        Assert.NotNull(await harness.Pipeline.GetPermissionAsync(
            $"codex-action:{EventPipeline.DeduplicationCapacity:x24}",
            CancellationToken.None));
    }

    [Fact]
    public async Task PersistedPayloadNeverContainsRawHookContent()
    {
        await using var harness = await Harness.CreateAsync(
            PermissionNotificationPolicy.AlwaysNotify);
        await harness.Pipeline.AcceptAsync(Permission(), CancellationToken.None);

        var serialized = JsonSerializer.Serialize(harness.Store.Events);
        Assert.DoesNotContain("tool_input", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-session", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-turn", serialized, StringComparison.Ordinal);
    }

    private static SanitizedActionRequiredEvent Permission(
        string toolUseHash = "abcdef123456",
        string eventId = "codex-action:abcdefabcdefabcdefabcdef") => new()
        {
            EventId = eventId,
            SessionIdHash = SessionHash,
            TurnIdHash = TurnHash,
            ToolUseIdHash = toolUseHash,
            ToolCategory = AgentToolCategories.Command,
            Project = "AgentBell",
            OccurredAt = Start,
        };

    private static SanitizedPostToolUseEvent PostToolUse() => new()
    {
        SessionIdHash = SessionHash,
        TurnIdHash = TurnHash,
        ToolUseIdHash = "abcdef123456",
        ToolCategory = AgentToolCategories.Command,
        OccurredAt = Start,
    };

    private static CodexStopHookPayload Stop() => new()
    {
        HookEventName = "Stop",
        SessionId = "session",
        TurnId = "turn",
        LastAssistantMessage = "Completed normally.",
    };

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(RecordingStore store, RecordingPublisher publisher, EventPipeline pipeline)
        {
            Store = store;
            Publisher = publisher;
            Pipeline = pipeline;
        }

        public RecordingStore Store { get; }

        public RecordingPublisher Publisher { get; }

        public EventPipeline Pipeline { get; }

        public static async Task<Harness> CreateAsync(
            PermissionNotificationPolicy policy = PermissionNotificationPolicy.Off,
            bool notifyActionRequired = true)
        {
            var clock = new ManualTimeProvider(Start);
            var store = new RecordingStore();
            var publisher = new RecordingPublisher();
            var settings = new DesktopNotificationSettingsState();
            settings.Update(new DesktopNotificationSettings
            {
                NotifyActionRequired = notifyActionRequired,
                PermissionNotificationPolicy = policy,
            });
            var pipeline = new EventPipeline(
                store,
                new CodexEventTransformer(clock),
                publisher,
                clock,
                settings);
            await pipeline.InitializeAsync(CancellationToken.None);
            return new Harness(store, publisher, pipeline);
        }

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    private sealed class RecordingStore : IEventStore
    {
        public IReadOnlyList<AgentEvent> Events { get; private set; } = [];

        public Task<EventStoreLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EventStoreLoadResult(Events, true, false));

        public Task<bool> SaveAsync(
            IReadOnlyList<AgentEvent> events,
            CancellationToken cancellationToken)
        {
            Events = events.ToArray();
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly ConcurrentQueue<AgentEvent> _events = new();

        public IReadOnlyList<AgentEvent> Events => _events.ToArray();

        public ValueTask PublishAsync(
            AgentEvent agentEvent,
            CancellationToken cancellationToken)
        {
            _events.Enqueue(agentEvent);
            return ValueTask.CompletedTask;
        }
    }
}
