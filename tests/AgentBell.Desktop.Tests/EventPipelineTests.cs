using System.Reflection;
using AgentBell.Contracts;

namespace AgentBell.Desktop.Tests;

public sealed class EventPipelineTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptAsync_GenericCompletion_PreservesEventIdAndProjectsLegacyShape()
    {
        var pipeline = await CreatePipelineAsync(new InMemoryEventStore());
        var submission = Completion("codex:event-one");

        var result = await pipeline.AcceptAsync(submission, CancellationToken.None);
        var accepted = Assert.IsType<AgentEvent>(result.Event);

        Assert.Equal("codex:event-one", accepted.EventId);
        Assert.Equal("codex", accepted.Agent);
        Assert.Equal(AgentEventCategories.Completion, accepted.Category);
        Assert.Equal(AgentActionTypes.None, accepted.ActionType);
        Assert.Equal(1, accepted.Sequence);
    }

    [Fact]
    public async Task AcceptAsync_GenericActionRequired_ProjectsExistingWireShape()
    {
        var pipeline = await CreatePipelineAsync(new InMemoryEventStore());

        var result = await pipeline.AcceptAsync(
            ActionRequired("claude-code:event-one", ProviderIds.ClaudeCode),
            CancellationToken.None);
        var accepted = Assert.IsType<AgentEvent>(result.Event);

        Assert.Equal("claude-code", accepted.Agent);
        Assert.Equal(AgentEventCategories.ActionRequired, accepted.Category);
        Assert.Equal(AgentActionTypes.InputRequired, accepted.ActionType);
        Assert.Null(accepted.Summary);
    }

    [Fact]
    public async Task AcceptAsync_DuplicateEvent_ReturnsAcceptedWithoutSavingOrConsumingSequenceTwice()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var submission = Completion("codex:duplicate");

        var first = await pipeline.AcceptAsync(submission, CancellationToken.None);
        var second = await pipeline.AcceptAsync(submission, CancellationToken.None);
        var next = await pipeline.AcceptAsync(Completion("codex:next"), CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(1, first.Event!.Sequence);
        Assert.Equal(2, next.Event!.Sequence);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task AcceptAsync_ConcurrentSameEvent_SavesAndPublishesExactlyOnce()
    {
        var store = new InMemoryEventStore();
        var publisher = new CollectingEventPublisher();
        var pipeline = await CreatePipelineAsync(store, publisher);
        var submission = Completion("codex:concurrent-duplicate");

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            pipeline.AcceptAsync(submission, CancellationToken.None)));

        Assert.Equal(1, results.Count(result => !result.IsDuplicate));
        Assert.Single(store.Snapshot);
        Assert.Single(publisher.Events);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task AcceptAsync_NotificationNone_DoesNotPersistOrPublish()
    {
        var store = new InMemoryEventStore();
        var publisher = new CollectingEventPublisher();
        var pipeline = await CreatePipelineAsync(store, publisher);
        var normalized = CreateNormalized("codex:suppressed", ProviderIds.Codex);
        var submission = new EventPipelineSubmission(
            ProviderIds.Codex,
            normalized,
            Suppressed(),
            NoLifecycle());

        var result = await pipeline.AcceptAsync(submission, CancellationToken.None);

        Assert.Null(result.Event);
        Assert.Empty(store.Snapshot);
        Assert.Empty(publisher.Events);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task AcceptAsync_MoreThan100Events_RetainsOnlyNewest100()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);

        for (var index = 1; index <= 105; index++)
        {
            await pipeline.AcceptAsync(Completion($"codex:event-{index}"), CancellationToken.None);
        }

        Assert.Equal(100, store.Snapshot.Count);
        Assert.Equal(6, store.Snapshot.Min(item => item.Sequence));
        Assert.Equal(105, store.Snapshot.Max(item => item.Sequence));
    }

    [Fact]
    public async Task InitializeAsync_RestoresDeduplicationAndContinuesMaximumSequence()
    {
        var store = new InMemoryEventStore(
            [TestEventFactory.Create("codex:older", 40), TestEventFactory.Create("codex:restored", 42)]);
        var pipeline = await CreatePipelineAsync(store);

        var duplicate = await pipeline.AcceptAsync(Completion("codex:restored"), CancellationToken.None);
        var next = await pipeline.AcceptAsync(Completion("codex:after-restart"), CancellationToken.None);

        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(43, next.Event!.Sequence);
    }

    [Fact]
    public async Task AcceptAsync_PersistenceFailure_DoesNotRejectOrForgetAcceptedEvent()
    {
        var store = new InMemoryEventStore { SaveSucceeds = false };
        var pipeline = await CreatePipelineAsync(store);
        var submission = Completion("codex:persistence-failure");

        var first = await pipeline.AcceptAsync(submission, CancellationToken.None);
        var duplicate = await pipeline.AcceptAsync(submission, CancellationToken.None);

        Assert.False(first.PersistenceSucceeded);
        Assert.True(duplicate.IsDuplicate);
    }

    [Fact]
    public async Task AcceptAsync_PersistenceFailureStillPublishesFromInMemoryState()
    {
        var store = new InMemoryEventStore { SaveSucceeds = false };
        var publisher = new CollectingEventPublisher();
        var pipeline = await CreatePipelineAsync(store, publisher);

        var result = await pipeline.AcceptAsync(
            Completion("codex:persistence-live"),
            CancellationToken.None);

        Assert.False(result.PersistenceSucceeded);
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task AcceptAsync_PublisherFailureDoesNotChangeAcceptance()
    {
        var publisher = new CollectingEventPublisher { ThrowOnPublish = true };
        var pipeline = await CreatePipelineAsync(new InMemoryEventStore(), publisher);

        var result = await pipeline.AcceptAsync(Completion("codex:publisher-failure"), CancellationToken.None);

        Assert.False(result.IsDuplicate);
        Assert.True(result.PersistenceSucceeded);
        Assert.Equal(1, result.Event!.Sequence);
    }

    [Fact]
    public async Task AcceptAsync_ConcurrentDifferentProviders_AssignsUniqueSequencesWithoutDedupCollision()
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var submissions = Enumerable.Range(1, 20)
            .SelectMany(index => new[]
            {
                Completion($"codex:event-{index}", ProviderIds.Codex),
                Completion($"claude-code:event-{index}", ProviderIds.ClaudeCode),
            });

        var results = await Task.WhenAll(submissions.Select(item =>
            pipeline.AcceptAsync(item, CancellationToken.None)));

        Assert.Equal(40, results.Select(result => result.Event!.Sequence).Distinct().Count());
        Assert.Equal(40, store.Snapshot.Count);
        Assert.Contains(store.Snapshot, item => item.Agent == "codex");
        Assert.Contains(store.Snapshot, item => item.Agent == "claude-code");
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsAtMost100AscendingEventsAfterSequence()
    {
        var pipeline = await CreatePipelineAsync(new InMemoryEventStore());
        for (var index = 1; index <= 105; index++)
        {
            await pipeline.AcceptAsync(Completion($"codex:history-{index}"), CancellationToken.None);
        }

        var history = await pipeline.GetHistoryAsync(0, CancellationToken.None);

        Assert.Equal(100, history.Events.Count);
        Assert.Equal(105, history.LatestSequence);
        Assert.Equal(100, history.EventCount);
        Assert.Equal(
            history.Events.OrderBy(item => item.Sequence).Select(item => item.Sequence),
            history.Events.Select(item => item.Sequence));
    }

    [Fact]
    public void PublicSubmissionBoundary_ContainsNoRawOrProviderDtoEscapeHatch()
    {
        var parameters = typeof(EventPipeline)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == nameof(EventPipeline.AcceptAsync))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        var properties = typeof(EventPipelineSubmission).GetProperties();

        Assert.Contains(typeof(EventPipelineSubmission), parameters);
        Assert.DoesNotContain(parameters, type => type.Name.Contains("Codex", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(System.Text.Json.JsonElement));
        Assert.DoesNotContain(properties, property =>
            typeof(System.Collections.IDictionary).IsAssignableFrom(property.PropertyType));
        Assert.DoesNotContain(properties, property => new[]
        {
            "Prompt", "Command", "ToolInput", "ToolOutput", "RawPayload", "RawJson",
        }.Contains(property.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void SubmissionBoundary_RejectsConflictingSemanticPresentationBeforePipelineExecution()
    {
        var normalized = CreateNormalized("codex:conflicting-presentation", ProviderIds.Codex);

        Assert.Throws<ArgumentException>(() => new EventPipelineSubmission(
            ProviderIds.Codex,
            normalized,
            new NotificationDecision(
                NotificationKind.ActionRequired,
                PresentationKind.InputRequired,
                NotificationReason.ConfirmedActionRequired),
            NoLifecycle()));
    }

    private static async Task<EventPipeline> CreatePipelineAsync(
        InMemoryEventStore store,
        IEventPublisher? publisher = null)
    {
        var pipeline = new EventPipeline(store, eventPublisher: publisher);
        await pipeline.InitializeAsync(CancellationToken.None);
        return pipeline;
    }

    internal static EventPipelineSubmission Completion(
        string eventId,
        ProviderId? providerId = null)
    {
        var provider = providerId ?? ProviderIds.Codex;
        var normalized = CreateNormalized(eventId, provider);
        return new EventPipelineSubmission(
            provider,
            normalized,
            new NotificationDecision(
                NotificationKind.Completion,
                PresentationKind.Completion,
                NotificationReason.CompletionObserved),
            NoLifecycle());
    }

    internal static EventPipelineSubmission ActionRequired(string eventId, ProviderId providerId)
    {
        var normalized = new NormalizedAgentEvent(
            providerId,
            new SourceEventKind("input-required"),
            SemanticEventKind.InputRequired,
            FixedTime,
            eventId,
            "abcdef123456",
            "123456abcdef",
            null,
            "AgentBell",
            null,
            AgentToolCategories.None,
            null,
            SemanticReliability.Reliable,
            ActionRequirement.Required);
        return new EventPipelineSubmission(
            providerId,
            normalized,
            new NotificationDecision(
                NotificationKind.ActionRequired,
                PresentationKind.InputRequired,
                NotificationReason.ConfirmedActionRequired),
            NoLifecycle());
    }

    internal static NormalizedAgentEvent CreateNormalized(string eventId, ProviderId providerId) => new(
        providerId,
        new SourceEventKind("turn-completed"),
        SemanticEventKind.TurnCompleted,
        FixedTime,
        eventId,
        "abcdef123456",
        "123456abcdef",
        null,
        "AgentBell",
        "Completed 🔔",
        AgentToolCategories.None,
        null,
        SemanticReliability.Reliable,
        ActionRequirement.None);

    internal static NotificationDecision Suppressed() => new(
        NotificationKind.None,
        null,
        NotificationReason.PolicySuppressed);

    internal static LifecycleDirective NoLifecycle() => new(
        LifecycleDirectiveKind.None,
        null,
        null,
        null,
        null,
        null);
}
