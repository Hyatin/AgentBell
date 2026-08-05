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
        IDesktopDiagnosticLogger diagnosticLogger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(diagnosticLogger);

        var stopwatch = Stopwatch.StartNew();
        EventAcceptanceResult? acceptance = null;
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
            if (parseResult.Status == StopPayloadStatus.Invalid)
            {
                statusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (parseResult.Status == StopPayloadStatus.Ignored)
            {
                statusCode = StatusCodes.Status204NoContent;
                return;
            }

            acceptance = await pipeline.AcceptAsync(
                parseResult.Payload!,
                context.RequestAborted).ConfigureAwait(false);
            persistenceSucceeded = acceptance.PersistenceSucceeded;
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
                acceptance,
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

    private static StopPayloadParseResult ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new StopPayloadParseResult(StopPayloadStatus.Invalid, null);
            }

            if (!document.RootElement.TryGetProperty("hook_event_name", out var eventName))
            {
                return new StopPayloadParseResult(StopPayloadStatus.Ignored, null);
            }

            if (eventName.ValueKind != JsonValueKind.String)
            {
                return new StopPayloadParseResult(StopPayloadStatus.Invalid, null);
            }

            if (!string.Equals(eventName.GetString(), "Stop", StringComparison.Ordinal))
            {
                return new StopPayloadParseResult(StopPayloadStatus.Ignored, null);
            }

            var payload = JsonSerializer.Deserialize<CodexStopHookPayload>(json, SerializerOptions);
            return payload is null
                ? new StopPayloadParseResult(StopPayloadStatus.Invalid, null)
                : new StopPayloadParseResult(StopPayloadStatus.Accepted, payload);
        }
        catch (JsonException)
        {
            return new StopPayloadParseResult(StopPayloadStatus.Invalid, null);
        }
    }

    private static bool HasUtf8ByteOrderMark(byte[] buffer, int length) =>
        length >= 3
        && buffer[0] == 0xEF
        && buffer[1] == 0xBB
        && buffer[2] == 0xBF;

    private static void TryRecordDiagnostic(
        IDesktopDiagnosticLogger logger,
        EventAcceptanceResult? acceptance,
        int statusCode,
        bool persistenceSucceeded,
        TimeSpan elapsed)
    {
        try
        {
            logger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = acceptance is null ? null : "codex-stop",
                ThreadIdHash = acceptance?.Event.ThreadIdHash,
                TurnIdHash = acceptance?.Event.TurnIdHash,
                IsDuplicate = acceptance?.IsDuplicate ?? false,
                HttpStatusCode = statusCode,
                ElapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds),
                PersistenceSucceeded = persistenceSucceeded,
                EventCount = acceptance?.EventCount ?? 0,
            });
        }
        catch
        {
            // Diagnostics cannot affect the HTTP result.
        }
    }

    private enum RequestBodyStatus
    {
        Valid,
        Empty,
        TooLarge,
        InvalidUtf8,
    }

    private enum StopPayloadStatus
    {
        Accepted,
        Ignored,
        Invalid,
    }

    private sealed record RequestBodyReadResult(RequestBodyStatus Status, string? Json);

    private sealed record StopPayloadParseResult(
        StopPayloadStatus Status,
        CodexStopHookPayload? Payload);
}
