using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;
using Microsoft.AspNetCore.Http;

namespace AgentBell.Desktop;

/// <summary>Defines the exact HTTP contract already used by the M0 Hook.</summary>
public static class DesktopHttpContract
{
    /// <summary>The loopback-only M0-compatible ingestion path.</summary>
    public const string EventsPath = "/api/v1/events/codex";

    /// <summary>The maximum accepted request-body size.</summary>
    public const int MaxRequestBodyBytes = 1024 * 1024;
}

/// <summary>Handles untrusted M0 Hook HTTP requests without retaining raw payload content.</summary>
public static class CodexEventIngestion
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Processes one POST request and writes only an HTTP status code.</summary>
    public static async Task HandleAsync(
        HttpContext context,
        EventPipeline pipeline,
        CodexPipelineSubmissionFactory submissionFactory,
        IDesktopDiagnosticLogger diagnosticLogger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(submissionFactory);
        ArgumentNullException.ThrowIfNull(diagnosticLogger);

        var stopwatch = Stopwatch.StartNew();
        EventPipelineResult? pipelineResult = null;
        string? compatibilityEventType = null;
        var statusCode = StatusCodes.Status500InternalServerError;
        var persistenceSucceeded = true;
        try
        {
            if (!HasJsonContentType(context.Request.ContentType))
            {
                statusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            if (context.Request.ContentLength > DesktopHttpContract.MaxRequestBodyBytes)
            {
                statusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            var readResult = await ReadBodyAsync(
                context.Request.Body,
                context.RequestAborted).ConfigureAwait(false);
            if (readResult.Status == RequestBodyStatus.TooLarge)
            {
                statusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (readResult.Status is RequestBodyStatus.Empty or RequestBodyStatus.InvalidUtf8)
            {
                statusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var parseResult = ParsePayload(readResult.Json!);
            if (parseResult.Status == IngestionPayloadStatus.Invalid)
            {
                statusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (parseResult.Status == IngestionPayloadStatus.Ignored)
            {
                statusCode = StatusCodes.Status204NoContent;
                return;
            }

            EventPipelineSubmission submission;
            if (parseResult.PostToolUse is not null)
            {
                compatibilityEventType = SanitizedPostToolUseEvent.PostToolUseEventType;
                submission = submissionFactory.Create(parseResult.PostToolUse);
            }
            else if (parseResult.ActionRequired is not null)
            {
                compatibilityEventType = SanitizedActionRequiredEvent.PermissionRequestEventType;
                submission = submissionFactory.Create(parseResult.ActionRequired);
            }
            else
            {
                compatibilityEventType = "codex-stop";
                submission = submissionFactory.Create(parseResult.Stop!);
            }

            pipelineResult = await pipeline.AcceptAsync(
                submission,
                context.RequestAborted).ConfigureAwait(false);
            persistenceSucceeded = pipelineResult.PersistenceSucceeded;
            statusCode = StatusCodes.Status202Accepted;
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            statusCode = StatusCodes.Status413PayloadTooLarge;
            persistenceSucceeded = false;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            statusCode = StatusCodes.Status408RequestTimeout;
            persistenceSucceeded = false;
        }
        catch
        {
            statusCode = StatusCodes.Status500InternalServerError;
            persistenceSucceeded = false;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.StatusCode = statusCode;
            TryRecordDiagnostic(
                diagnosticLogger,
                pipelineResult,
                compatibilityEventType,
                statusCode,
                persistenceSucceeded,
                stopwatch.Elapsed);
        }
    }

    private static bool HasJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            return false;
        }

        return string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<RequestBodyReadResult> ReadBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DesktopHttpContract.MaxRequestBodyBytes + 1];
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead > DesktopHttpContract.MaxRequestBodyBytes)
        {
            return new RequestBodyReadResult(RequestBodyStatus.TooLarge, null);
        }

        var offset = HasUtf8ByteOrderMark(buffer, bytesRead) ? 3 : 0;
        if (bytesRead == offset)
        {
            return new RequestBodyReadResult(RequestBodyStatus.Empty, null);
        }

        try
        {
            var json = StrictUtf8.GetString(buffer, offset, bytesRead - offset);
            return string.IsNullOrWhiteSpace(json)
                ? new RequestBodyReadResult(RequestBodyStatus.Empty, null)
                : new RequestBodyReadResult(RequestBodyStatus.Valid, json);
        }
        catch (DecoderFallbackException)
        {
            return new RequestBodyReadResult(RequestBodyStatus.InvalidUtf8, null);
        }
    }

    private static IngestionPayloadParseResult ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidPayload();
            }

            if (document.RootElement.TryGetProperty("eventType", out var eventType))
            {
                if (eventType.ValueKind != JsonValueKind.String)
                {
                    return InvalidPayload();
                }

                var eventTypeValue = eventType.GetString();
                if (string.Equals(
                    eventTypeValue,
                    SanitizedActionRequiredEvent.PermissionRequestEventType,
                    StringComparison.Ordinal))
                {
                    var action = JsonSerializer.Deserialize<SanitizedActionRequiredEvent>(
                        json,
                        SerializerOptions);
                    return action is not null && IsValidSanitizedPermissionRequest(action)
                        ? new IngestionPayloadParseResult(
                            IngestionPayloadStatus.Accepted,
                            null,
                            action,
                            null)
                        : InvalidPayload();
                }

                if (!string.Equals(
                    eventTypeValue,
                    SanitizedPostToolUseEvent.PostToolUseEventType,
                    StringComparison.Ordinal))
                {
                    return IgnoredPayload();
                }

                var postToolUse = JsonSerializer.Deserialize<SanitizedPostToolUseEvent>(
                    json,
                    SerializerOptions);
                return postToolUse is not null && IsValidSanitizedPostToolUse(postToolUse)
                    ? new IngestionPayloadParseResult(
                        IngestionPayloadStatus.Accepted,
                        null,
                        null,
                        postToolUse)
                    : InvalidPayload();
            }

            if (!document.RootElement.TryGetProperty("hook_event_name", out var eventName))
            {
                return IgnoredPayload();
            }

            if (eventName.ValueKind != JsonValueKind.String)
            {
                return InvalidPayload();
            }

            if (!string.Equals(eventName.GetString(), "Stop", StringComparison.Ordinal))
            {
                return IgnoredPayload();
            }

            var payload = JsonSerializer.Deserialize<CodexStopHookPayload>(json, SerializerOptions);
            return payload is null
                ? InvalidPayload()
                : new IngestionPayloadParseResult(
                    IngestionPayloadStatus.Accepted,
                    payload,
                    null,
                    null);
        }
        catch (JsonException)
        {
            return InvalidPayload();
        }
    }

    private static bool IsValidSanitizedPermissionRequest(SanitizedActionRequiredEvent value) =>
        value.EventType == SanitizedActionRequiredEvent.PermissionRequestEventType
        && value.EventId.StartsWith("codex-action:", StringComparison.Ordinal)
        && IsHex(value.EventId.AsSpan("codex-action:".Length), 24)
        && IsOptionalHash(value.SessionIdHash)
        && IsOptionalHash(value.TurnIdHash)
        && IsOptionalHash(value.ToolUseIdHash)
        && value.Category == AgentEventCategories.ActionRequired
        && value.ActionType == AgentActionTypes.PermissionRequired
        && IsAllowedToolCategory(value.ToolCategory)
        && value.OccurredAt > DateTimeOffset.MinValue
        && IsProjectBasename(value.Project);

    private static bool IsValidSanitizedPostToolUse(SanitizedPostToolUseEvent value) =>
        value.EventType == SanitizedPostToolUseEvent.PostToolUseEventType
        && IsOptionalHash(value.SessionIdHash)
        && IsOptionalHash(value.TurnIdHash)
        && IsOptionalHash(value.ToolUseIdHash)
        && IsAllowedToolCategory(value.ToolCategory)
        && value.OccurredAt > DateTimeOffset.MinValue;

    private static bool IsOptionalHash(string? value) =>
        value is null || IsHex(value.AsSpan(), 12);

    private static bool IsHex(ReadOnlySpan<char> value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedToolCategory(string value) => value is
        AgentToolCategories.Command
        or AgentToolCategories.FileChange
        or AgentToolCategories.NetworkAccess
        or AgentToolCategories.ExternalTool
        or AgentToolCategories.ComputerControl
        or AgentToolCategories.Other;

    private static bool IsProjectBasename(string? value) =>
        value is null
        || (value.Length is > 0 and <= 256
            && value.IndexOfAny(['\\', '/', ':']) < 0
            && value is not "." and not "..");

    private static IngestionPayloadParseResult InvalidPayload() =>
        new(IngestionPayloadStatus.Invalid, null, null, null);

    private static IngestionPayloadParseResult IgnoredPayload() =>
        new(IngestionPayloadStatus.Ignored, null, null, null);

    private static bool HasUtf8ByteOrderMark(byte[] buffer, int length) =>
        length >= 3
        && buffer[0] == 0xEF
        && buffer[1] == 0xBB
        && buffer[2] == 0xBF;

    private static void TryRecordDiagnostic(
        IDesktopDiagnosticLogger logger,
        EventPipelineResult? pipelineResult,
        string? compatibilityEventType,
        int statusCode,
        bool persistenceSucceeded,
        TimeSpan elapsed)
    {
        try
        {
            logger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = compatibilityEventType,
                ThreadIdHash = compatibilityEventType ==
                    SanitizedActionRequiredEvent.PermissionRequestEventType
                    ? null
                    : pipelineResult?.Event?.ThreadIdHash,
                SessionIdHash = compatibilityEventType switch
                {
                    SanitizedPostToolUseEvent.PostToolUseEventType =>
                        pipelineResult?.LifecycleResolution.SessionIdHash,
                    SanitizedActionRequiredEvent.PermissionRequestEventType =>
                        pipelineResult?.NormalizedEvent?.SessionIdHash,
                    _ => null,
                },
                TurnIdHash = pipelineResult?.LifecycleResolution.TurnIdHash
                    ?? pipelineResult?.NormalizedEvent?.TurnIdHash,
                ToolCategory = pipelineResult?.LifecycleResolution.ToolCategory
                    ?? pipelineResult?.NormalizedEvent?.ToolCategory,
                EventIdHash = pipelineResult?.NormalizedEvent is null
                    ? null
                    : IdentifierHash.CreateFingerprint(pipelineResult.NormalizedEvent.EventId),
                ClassifiedAs = ToLegacyActionType(
                    compatibilityEventType,
                    pipelineResult?.NormalizedEvent),
                MatchedRuleId = pipelineResult?.NormalizedEvent?.Classification?.RuleId,
                ConfidenceBand = ToLegacyConfidenceBand(
                    compatibilityEventType,
                    pipelineResult?.NormalizedEvent?.Classification?.Confidence),
                IsDuplicate = pipelineResult?.IsDuplicate ?? false,
                HttpStatusCode = statusCode,
                ElapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds),
                PersistenceSucceeded = persistenceSucceeded,
                EventCount = compatibilityEventType == SanitizedPostToolUseEvent.PostToolUseEventType
                    ? 0
                    : pipelineResult?.EventCount ?? 0,
                Result = compatibilityEventType != SanitizedPostToolUseEvent.PostToolUseEventType
                    ? pipelineResult?.LifecycleState switch
                    {
                        EventLifecycleState.Tracked => "permission_observed_off",
                        EventLifecycleState.Delivered => "permission_published",
                        _ => null,
                    }
                    : pipelineResult?.LifecycleResolution.Matched == true
                        ? pipelineResult.LifecycleResolution.DeliveredResolved > 0
                            ? "resolved_published"
                            : "resolved_observed"
                        : "no_match",
            });
        }
        catch
        {
            // Diagnostics cannot affect the HTTP result.
        }
    }

    private static string? ToLegacyActionType(
        string? compatibilityEventType,
        NormalizedAgentEvent? normalizedEvent)
    {
        if (compatibilityEventType == SanitizedActionRequiredEvent.PermissionRequestEventType)
        {
            return AgentActionTypes.PermissionRequired;
        }

        return normalizedEvent?.SemanticEventKind switch
        {
            SemanticEventKind.TurnCompleted => AgentActionTypes.None,
            SemanticEventKind.PermissionObserved or SemanticEventKind.PermissionRequired =>
                AgentActionTypes.PermissionRequired,
            SemanticEventKind.InputRequired => AgentActionTypes.InputRequired,
            SemanticEventKind.ConfirmationRequired => AgentActionTypes.ConfirmationRequired,
            SemanticEventKind.AttentionRequired => AgentActionTypes.AttentionRequired,
            null => null,
            _ => null,
        };
    }

    private static string? ToLegacyConfidenceBand(
        string? compatibilityEventType,
        ClassificationConfidence? confidence) => confidence switch
        {
            ClassificationConfidence.High => "high",
            ClassificationConfidence.Medium => "medium",
            ClassificationConfidence.Low => "low",
            null when compatibilityEventType == SanitizedActionRequiredEvent.PermissionRequestEventType =>
                "structured",
            null when compatibilityEventType == "codex-stop" => "none",
            null => null,
            _ => null,
        };

    private enum RequestBodyStatus
    {
        Valid,
        Empty,
        TooLarge,
        InvalidUtf8,
    }

    private enum IngestionPayloadStatus
    {
        Accepted,
        Ignored,
        Invalid,
    }

    private sealed record RequestBodyReadResult(RequestBodyStatus Status, string? Json);

    private sealed record IngestionPayloadParseResult(
        IngestionPayloadStatus Status,
        CodexStopHookPayload? Stop,
        SanitizedActionRequiredEvent? ActionRequired,
        SanitizedPostToolUseEvent? PostToolUse);
}
