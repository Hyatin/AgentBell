using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class EventPipelineTests
{
    [Fact]
    public async Task AcceptAsync_DuplicateEvent_ReturnsAcceptedWithoutSavingTwice()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var payload = CreatePayload("session-1", "turn-1");

        var first = await pipeline.AcceptAsync(payload, CancellationToken.None);
        var second = await pipeline.AcceptAsync(payload, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(1, first.Event.Sequence);
        Assert.Equal(1, store.SaveCount);
        Assert.Single(store.Snapshot);
    }

    [Fact]
    public async Task AcceptAsync_MoreThan100Events_RetainsOnlyNewest100()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);

        for (var index = 1; index <= 105; index++)
        {
            await pipeline.AcceptAsync(
                CreatePayload("session", $"turn-{index}"),
                CancellationToken.None);
        }

        Assert.Equal(100, store.Snapshot.Count);
        Assert.Equal(6, store.Snapshot.Min(item => item.Sequence));
        Assert.Equal(105, store.Snapshot.Max(item => item.Sequence));
    }

    [Fact]
    public async Task InitializeAsync_RestoresDeduplicationAndContinuesMaximumSequence()
    {
        var transformer = new CodexEventTransformer();
        var restoredPayload = CreatePayload("session-restored", "turn-restored");
        var restoredCandidate = transformer.Transform(restoredPayload) with { Sequence = 42 };
        var store = new InMemoryEventStore(
            [TestEventFactory.Create("older", 40), restoredCandidate]);
        var pipeline = new EventPipeline(store, transformer);

        await pipeline.InitializeAsync(CancellationToken.None);
        var duplicate = await pipeline.AcceptAsync(restoredPayload, CancellationToken.None);
        var next = await pipeline.AcceptAsync(
            CreatePayload("session-next", "turn-next"),
            CancellationToken.None);

        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(43, next.Event.Sequence);
    }

    [Fact]
    public async Task AcceptAsync_PersistenceFailure_DoesNotRejectOrForgetAcceptedEvent()
    {
        var store = new InMemoryEventStore { SaveSucceeds = false };
        var pipeline = await CreatePipelineAsync(store);
        var payload = CreatePayload("session", "turn");

        var first = await pipeline.AcceptAsync(payload, CancellationToken.None);
        var duplicate = await pipeline.AcceptAsync(payload, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.False(first.PersistenceSucceeded);
        Assert.True(duplicate.IsDuplicate);
    }

    [Fact]
    public async Task AcceptAsync_ConcurrentSameEvent_SavesExactlyOnce()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var payload = CreatePayload("concurrent-session", "concurrent-turn");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 40)
                .Select(_ => pipeline.AcceptAsync(payload, CancellationToken.None)));

        Assert.Equal(1, results.Count(result => !result.IsDuplicate));
        Assert.Single(store.Snapshot);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task AcceptAsync_ConcurrentDifferentEvents_AssignsUniqueMonotonicSequences()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);

        var results = await Task.WhenAll(
            Enumerable.Range(1, 40)
                .Select(index => pipeline.AcceptAsync(
                    CreatePayload("session", $"turn-{index}"),
                    CancellationToken.None)));

        Assert.Equal(40, results.Select(result => result.Event.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 40).Select(value => (long)value),
            results.Select(result => result.Event.Sequence).OrderBy(value => value));
        Assert.Equal(40, store.Snapshot.Count);
    }

    [Fact]
    public async Task AcceptAsync_NewEventPublishesOnceButDuplicateDoesNotPublish()
    {
        var store = new InMemoryEventStore();
        var publisher = new CollectingEventPublisher();
        var pipeline = new EventPipeline(store, new CodexEventTransformer(), publisher);
        await pipeline.InitializeAsync(CancellationToken.None);
        var payload = CreatePayload("session-publish", "turn-publish");

        var first = await pipeline.AcceptAsync(payload, CancellationToken.None);
        var duplicate = await pipeline.AcceptAsync(payload, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        var published = Assert.Single(publisher.Events);
        Assert.Equal(1, published.Sequence);
        Assert.Equal(first.Event.EventId, published.EventId);
    }

    [Fact]
    public async Task AcceptAsync_PersistenceFailureStillPublishesFromInMemoryState()
    {
        var store = new InMemoryEventStore { SaveSucceeds = false };
        var publisher = new CollectingEventPublisher();
        var pipeline = new EventPipeline(store, new CodexEventTransformer(), publisher);
        await pipeline.InitializeAsync(CancellationToken.None);

        var result = await pipeline.AcceptAsync(
            CreatePayload("session-live", "turn-live"),
            CancellationToken.None);

        Assert.False(result.PersistenceSucceeded);
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task AcceptAsync_PublisherFailureDoesNotChangeM1Acceptance()
    {
        var publisher = new CollectingEventPublisher { ThrowOnPublish = true };
        var pipeline = new EventPipeline(
            new InMemoryEventStore(),
            new CodexEventTransformer(),
            publisher);
        await pipeline.InitializeAsync(CancellationToken.None);

        var result = await pipeline.AcceptAsync(
            CreatePayload("session-failure", "turn-failure"),
            CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.True(result.PersistenceSucceeded);
        Assert.Equal(1, result.Event.Sequence);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsAtMost100AscendingEventsAfterSequence()
    {
        var pipeline = await CreatePipelineAsync(new InMemoryEventStore());
        for (var index = 1; index <= 105; index++)
        {
            await pipeline.AcceptAsync(
                CreatePayload("history-session", $"history-turn-{index}"),
                CancellationToken.None);
        }

        var history = await pipeline.GetHistoryAsync(0, CancellationToken.None);

        Assert.Equal(100, history.Events.Count);
        Assert.Equal(105, history.LatestSequence);
        Assert.Equal(100, history.EventCount);
        Assert.Equal(
            history.Events.OrderBy(item => item.Sequence).Select(item => item.Sequence),
            history.Events.Select(item => item.Sequence));
        Assert.All(history.Events, item => Assert.True(item.Sequence > 0));
    }

    private static async Task<EventPipeline> CreatePipelineAsync(InMemoryEventStore store)
    {
        var pipeline = new EventPipeline(store, new CodexEventTransformer());
        await pipeline.InitializeAsync(CancellationToken.None);
        return pipeline;
    }

    private static CodexStopHookPayload CreatePayload(string sessionId, string turnId) =>
        new()
        {
            HookEventName = "Stop",
            SessionId = sessionId,
            TurnId = turnId,
        };
}
