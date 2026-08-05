using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentBell.Desktop.Tests;

public sealed class CodexEventIngestionTests
{
    [Fact]
    public async Task HandleAsync_NormalStopEvent_Returns202AndPersistsSanitizedEvent()
    {
        const string Json = """
            {
              "hook_event_name":"Stop",
              "session_id":"private-session",
              "turn_id":"private-turn",
              "cwd":"C:\\Private\\AgentBell",
              "last_assistant_message":"完成了 M1 🔔",
              "stop_hook_active":false,
              "permission_mode":"default",
              "model":"gpt-5",
              "input-messages":["must be ignored"],
              "future_field":true
            }
            """;

        var result = await SendAsync(Encoding.UTF8.GetBytes(Json), "application/json; charset=utf-8");

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        var persisted = Assert.Single(result.Store.Snapshot);
        Assert.Equal("AgentBell", persisted.Project);
        Assert.Equal("完成了 M1 🔔", persisted.Summary);
        Assert.Equal(12, persisted.ThreadIdHash?.Length);
        Assert.Equal(12, persisted.TurnIdHash?.Length);
        Assert.DoesNotContain("private-session", persisted.EventId, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn", persisted.EventId, StringComparison.Ordinal);

        var diagnostic = Assert.Single(result.Logger.Events);
        var diagnosticJson = JsonSerializer.Serialize(diagnostic);
        Assert.Equal("codex-stop", diagnostic.EventType);
        Assert.Equal(202, diagnostic.HttpStatusCode);
        Assert.DoesNotContain(Json, diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-turn", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Private\\AgentBell", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("完成了 M1", diagnosticJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_StopWithMissingOptionalFields_Returns202()
    {
        var result = await SendJsonAsync("{\"hook_event_name\":\"Stop\"}");

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        var item = Assert.Single(result.Store.Snapshot);
        Assert.Null(item.Project);
        Assert.Null(item.Summary);
        Assert.Null(item.ThreadIdHash);
        Assert.Null(item.TurnIdHash);
    }

    [Theory]
    [InlineData("{\"hook_event_name\":\"PostToolUse\"}")]
    [InlineData("{\"future_field\":true}")]
    public async Task HandleAsync_NonStopOrMissingEventName_Returns204AndDoesNotPersist(string json)
    {
        var result = await SendJsonAsync(json);

        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
        Assert.Empty(result.Store.Snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"hook_event_name\":")]
    [InlineData("[]")]
    [InlineData("{\"hook_event_name\":42}")]
    [InlineData("{\"hook_event_name\":\"Stop\",\"stop_hook_active\":\"wrong\"}")]
    public async Task HandleAsync_InvalidOrEmptyJson_Returns400(string json)
    {
        var result = await SendJsonAsync(json);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(result.Store.Snapshot);
    }

    [Fact]
    public async Task HandleAsync_OverOneMiB_Returns413()
    {
        var result = await SendAsync(
            new byte[DesktopHttpContract.MaxRequestBodyBytes + 1],
            "application/json");

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Empty(result.Store.Snapshot);
    }

    [Fact]
    public async Task HandleAsync_InvalidUtf8_Returns400()
    {
        var result = await SendAsync([0xFF, 0xFE, 0x00], "application/json");

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(result.Store.Snapshot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task HandleAsync_UnsupportedContentType_Returns415(string? contentType)
    {
        var result = await SendAsync(
            Encoding.UTF8.GetBytes("{\"hook_event_name\":\"Stop\"}"),
            contentType);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
        Assert.Empty(result.Store.Snapshot);
    }

    [Fact]
    public async Task HandleAsync_DuplicateEvent_Returns202WithoutSecondSave()
    {
        const string Json = """
            {"hook_event_name":"Stop","session_id":"same-session","turn_id":"same-turn"}
            """;
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var logger = new CollectingDesktopDiagnosticLogger();

        var first = await SendAsync(Json, pipeline, store, logger);
        var second = await SendAsync(Json, pipeline, store, logger);

        Assert.Equal(202, first.StatusCode);
        Assert.Equal(202, second.StatusCode);
        Assert.Single(store.Snapshot);
        Assert.Equal(1, store.SaveCount);
        Assert.False(logger.Events[0].IsDuplicate);
        Assert.True(logger.Events[1].IsDuplicate);
    }

    [Fact]
    public async Task HandleAsync_PersistenceFailure_StillReturns202AndRecordsSanitizedFailure()
    {
        var store = new InMemoryEventStore { SaveSucceeds = false };
        var pipeline = await CreatePipelineAsync(store);
        var logger = new CollectingDesktopDiagnosticLogger();

        var result = await SendAsync(
            "{\"hook_event_name\":\"Stop\",\"session_id\":\"secret\"}",
            pipeline,
            store,
            logger);

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        var diagnostic = Assert.Single(logger.Events);
        Assert.False(diagnostic.PersistenceSucceeded);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    private static Task<IngestionRun> SendJsonAsync(string json) =>
        SendAsync(Encoding.UTF8.GetBytes(json), "application/json");

    private static async Task<IngestionRun> SendAsync(byte[] body, string? contentType)
    {
        var store = new InMemoryEventStore();
        var pipeline = await CreatePipelineAsync(store);
        var logger = new CollectingDesktopDiagnosticLogger();
        return await SendAsync(body, contentType, pipeline, store, logger);
    }

    private static Task<IngestionRun> SendAsync(
        string json,
        EventPipeline pipeline,
        InMemoryEventStore store,
        CollectingDesktopDiagnosticLogger logger) =>
        SendAsync(Encoding.UTF8.GetBytes(json), "application/json", pipeline, store, logger);

    private static async Task<IngestionRun> SendAsync(
        byte[] body,
        string? contentType,
        EventPipeline pipeline,
        InMemoryEventStore store,
        CollectingDesktopDiagnosticLogger logger)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body, writable: false);
        context.Response.Body = new MemoryStream();

        await CodexEventIngestion.HandleAsync(context, pipeline, logger);

        return new IngestionRun(context.Response.StatusCode, store, logger);
    }

    private static async Task<EventPipeline> CreatePipelineAsync(InMemoryEventStore store)
    {
        var pipeline = new EventPipeline(store, new CodexEventTransformer());
        await pipeline.InitializeAsync(CancellationToken.None);
        return pipeline;
    }

    private sealed record IngestionRun(
        int StatusCode,
        InMemoryEventStore Store,
        CollectingDesktopDiagnosticLogger Logger);
}
