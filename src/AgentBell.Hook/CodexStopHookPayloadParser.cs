using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Parses and validates a Codex Stop command Hook JSON object.</summary>
public interface ICodexStopHookPayloadParser
{
    /// <summary>Parses one raw JSON object without reading any referenced transcript file.</summary>
    CodexStopHookParseResult Parse(string rawJson);
}

/// <summary>Contains a parsed Stop Hook payload or a stable error code.</summary>
/// <param name="IsSuccess">Whether a supported Stop payload was parsed.</param>
/// <param name="Payload">The parsed payload when successful.</param>
/// <param name="ErrorCode">A stable error code when parsing failed.</param>
public sealed record CodexStopHookParseResult(
    bool IsSuccess,
    CodexStopHookPayload? Payload,
    string? ErrorCode);

/// <summary>Default <see cref="ICodexStopHookPayloadParser"/> implementation.</summary>
public sealed class CodexStopHookPayloadParser : ICodexStopHookPayloadParser
{
    private const string SupportedHookEventName = "Stop";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
    };

    /// <inheritdoc />
    public CodexStopHookParseResult Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Failure(HookErrorCodes.StopHookEmptyInput);
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions { MaxDepth = 32 });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(HookErrorCodes.InvalidJson);
            }

            if (!document.RootElement.TryGetProperty("hook_event_name", out var eventNameElement)
                || eventNameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(eventNameElement.GetString()))
            {
                return Failure(HookErrorCodes.MissingHookEventName);
            }

            if (!string.Equals(
                eventNameElement.GetString(),
                SupportedHookEventName,
                StringComparison.Ordinal))
            {
                return Failure(HookErrorCodes.UnsupportedHookEvent);
            }

            var payload = JsonSerializer.Deserialize<CodexStopHookPayload>(rawJson, SerializerOptions);
            return payload is null
                ? Failure(HookErrorCodes.InvalidJson)
                : new CodexStopHookParseResult(true, payload, null);
        }
        catch (JsonException)
        {
            return Failure(HookErrorCodes.InvalidJson);
        }
    }

    private static CodexStopHookParseResult Failure(string errorCode) =>
        new(false, null, errorCode);
}

