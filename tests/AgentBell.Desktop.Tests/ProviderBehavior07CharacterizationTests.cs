using System.Text.Json;
using AgentBell.Contracts;
using AgentBell.Hook;

namespace AgentBell.Desktop.Tests;

public sealed class ProviderBehavior07CharacterizationTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 7, 12, 34, 56, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(
        "Synthetic work completed.",
        "completed",
        "completion",
        "none",
        "codex:e18c78136e8e:04d186e38f9a")]
    [InlineData(
        "Please provide the synthetic choice.",
        "action_required",
        "action_required",
        "input_required",
        "codex-action:b6d7891a91c10840802e2b6c")]
    [InlineData(
        "Please confirm before I continue.",
        "action_required",
        "action_required",
        "confirmation_required",
        "codex-action:ce06fb39f016f59e85fe225a")]
    [InlineData(
        "I cannot continue until the synthetic fixture is supplied.",
        "action_required",
        "action_required",
        "attention_required",
        "codex-action:9e7e29b6848adae754f5c3c5")]
    public void StopVectors_Preserve07AgentEventWireShapeAndDeterministicIds(
        string message,
        string status,
        string category,
        string actionType,
        string eventId)
    {
        var payload = new CodexStopHookPayload
        {
            HookEventName = "Stop",
            SessionId = "stable-session",
            TurnId = "stable-turn",
            WorkingDirectory = "C:\\Synthetic Parent\\AgentBell",
            LastAssistantMessage = message,
        };
        var first = CreateTransformer().Transform(payload) with { Sequence = 42 };
        var second = CreateTransformer().Transform(payload) with { Sequence = 42 };

        Assert.Equal(eventId, first.EventId);
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal("codex", first.Agent);
        Assert.Equal(status, first.Status);
        Assert.Equal(category, first.Category);
        Assert.Equal(actionType, first.ActionType);
        Assert.Equal("none", first.ToolCategory);
        Assert.Equal("AgentBell", first.Project);
        Assert.Equal("e18c78136e8e", first.ThreadIdHash);
        Assert.Equal("04d186e38f9a", first.TurnIdHash);
        Assert.Equal(42, first.Sequence);
        Assert.Equal(FixedTime, first.OccurredAt);
        Assert.Equal(actionType == "none" ? message : null, first.Summary);
        Assert07WireShape(first);
    }

    [Fact]
    public async Task PermissionAlwaysNotify_Preserves07WireShapeAndGoldenEventId()
    {
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            PermissionNotificationPolicy = PermissionNotificationPolicy.AlwaysNotify,
        });
        var store = new InMemoryEventStore();
        var publisher = new CollectingEventPublisher();
        await using var pipeline = new EventPipeline(
            store,
            CreateTransformer(),
            publisher,
            new ManualTimeProvider(FixedTime),
            settings);
        await pipeline.InitializeAsync(CancellationToken.None);
        var sanitized = new PermissionRequestSanitizer().Sanitize(
            new CodexPermissionRequestPayload
            {
                HookEventName = "PermissionRequest",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                ToolUseId = "stable-tool-use",
                ToolName = "Bash",
                WorkingDirectory = "C:\\Synthetic Parent\\AgentBell",
            }).Event with
        {
            OccurredAt = FixedTime,
        };

        var accepted = await pipeline.AcceptAsync(sanitized, CancellationToken.None);

        Assert.False(accepted.IsDuplicate);
        Assert.Equal(PermissionLifecycleState.Published, accepted.PermissionState);
        Assert.Equal("codex-action:fa4afc83bf185000bb870ddb", accepted.Event.EventId);
        Assert.Equal("permission_required", accepted.Event.ActionType);
        Assert.Equal("command", accepted.Event.ToolCategory);
        Assert.Equal(1, accepted.Event.Sequence);
        Assert.Single(store.Snapshot);
        Assert.Single(publisher.Events);
        Assert07WireShape(accepted.Event);
    }

    [Fact]
    public async Task Lifecycle07_PostToolUseHasNoIndependentEventAndStopStillCompletes()
    {
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            PermissionNotificationPolicy = PermissionNotificationPolicy.AlwaysNotify,
        });
        var store = new InMemoryEventStore();
        await using var pipeline = new EventPipeline(
            store,
            CreateTransformer(),
            notificationSettings: settings);
        await pipeline.InitializeAsync(CancellationToken.None);
        var permission = new PermissionRequestSanitizer().Sanitize(
            new CodexPermissionRequestPayload
            {
                HookEventName = "PermissionRequest",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                ToolUseId = "stable-tool-use",
                ToolName = "Bash",
            }).Event with
        {
            OccurredAt = FixedTime,
        };
        var postResult = new PostToolUseSanitizer().Sanitize(
            new CodexPostToolUsePayload
            {
                HookEventName = "PostToolUse",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                ToolUseId = "stable-tool-use",
                ToolName = "Bash",
            });

        await pipeline.AcceptAsync(permission, CancellationToken.None);
        var resolution = await pipeline.ResolveAsync(postResult.Event, CancellationToken.None);

        Assert.True(resolution.Matched);
        Assert.Equal(0, resolution.ObservedResolved);
        Assert.Equal(1, resolution.PublishedResolved);
        Assert.False(JsonDocument.Parse(postResult.Json).RootElement.TryGetProperty("eventId", out _));
        var resolvedHistory = await pipeline.GetHistoryAsync(0, CancellationToken.None);
        var resolvedEvent = Assert.Single(resolvedHistory.Events);
        Assert.Equal(permission.EventId, resolvedEvent.EventId);
        Assert.NotNull(resolvedEvent.ResolvedAt);

        await pipeline.AcceptAsync(
            new CodexStopHookPayload
            {
                HookEventName = "Stop",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                LastAssistantMessage = "Synthetic work completed.",
            },
            CancellationToken.None);

        var history = await pipeline.GetHistoryAsync(0, CancellationToken.None);
        Assert.Equal(2, history.Events.Count);
        Assert.Equal(3, history.LatestSequence);
    }

    [Fact]
    public async Task PrivacySentinel_DoesNotEscapeSanitizerDesktopHistoryOrWirePayload()
    {
        const string Sentinel = "AGENTBELL_SECRET_SHOULD_NEVER_ESCAPE_7F3A";
        var sanitized = new PermissionRequestSanitizer().Sanitize(
            new CodexPermissionRequestPayload
            {
                HookEventName = "PermissionRequest",
                SessionId = "stable-session",
                TurnId = "stable-turn",
                ToolUseId = "stable-tool-use",
                ToolName = "Bash",
                WorkingDirectory = $"C:\\{Sentinel}\\AgentBell",
            });
        var settings = new DesktopNotificationSettingsState();
        settings.Update(new DesktopNotificationSettings
        {
            PermissionNotificationPolicy = PermissionNotificationPolicy.AlwaysNotify,
        });
        var store = new InMemoryEventStore();
        await using var pipeline = new EventPipeline(
            store,
            CreateTransformer(),
            notificationSettings: settings);
        await pipeline.InitializeAsync(CancellationToken.None);
        var accepted = await pipeline.AcceptAsync(sanitized.Event, CancellationToken.None);
        var desktopJson = JsonSerializer.Serialize(accepted.Event, WireOptions);
        var historyJson = JsonSerializer.Serialize(store.Snapshot, WireOptions);
        var webSocketJson = JsonSerializer.Serialize(
            new EventMessage { Payload = accepted.Event },
            WireOptions);

        Assert.DoesNotContain(Sentinel, sanitized.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, desktopJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, historyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, webSocketJson, StringComparison.Ordinal);
    }

    private static CodexEventTransformer CreateTransformer() =>
        new(new ManualTimeProvider(FixedTime));

    private static void Assert07WireShape(AgentEvent agentEvent)
    {
        var json = JsonSerializer.Serialize(new EventMessage { Payload = agentEvent }, WireOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("event", root.GetProperty("type").GetString());
        Assert.Equal(["payload", "type"], root.EnumerateObject().Select(item => item.Name).Order());
        var payload = root.GetProperty("payload");
        Assert.Equal(
            Expected07AgentEventProperties,
            payload.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("eventId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("occurredAt").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("sequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(
            agentEvent.Summary is null ? JsonValueKind.Null : JsonValueKind.String,
            payload.GetProperty("summary").ValueKind);
        Assert.False(payload.TryGetProperty("providerId", out _));
    }

    private static readonly string[] Expected07AgentEventProperties =
    [
        "actionType",
        "agent",
        "category",
        "eventId",
        "occurredAt",
        "project",
        "resolvedAt",
        "sequence",
        "status",
        "summary",
        "threadIdHash",
        "title",
        "toolCategory",
        "toolUseIdHash",
        "turnIdHash",
    ];
}
