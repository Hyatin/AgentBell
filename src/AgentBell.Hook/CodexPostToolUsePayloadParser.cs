using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Parses only allow-listed PostToolUse correlation metadata.</summary>
public interface ICodexPostToolUsePayloadParser
{
    /// <summary>Parses one official PostToolUse JSON object from standard input.</summary>
    CodexPostToolUseParseResult Parse(string rawJson);
}

/// <summary>Contains a safe in-memory payload or stable content-free error code.</summary>
public sealed record CodexPostToolUseParseResult(
    bool IsSuccess,
    CodexPostToolUsePayload? Payload,
    string? ErrorCode);

/// <summary>Default strict PostToolUse parser.</summary>
public sealed class CodexPostToolUsePayloadParser : ICodexPostToolUsePayloadParser
{
    private const string SupportedEventName = "PostToolUse";

    /// <inheritdoc />
    public CodexPostToolUseParseResult Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Failure(HookErrorCodes.PostToolUseHookEmptyInput);
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(HookErrorCodes.InvalidJson);
            }

            if (!TryOptionalString(root, "hook_event_name", out var eventName)
                || string.IsNullOrWhiteSpace(eventName))
            {
                return Failure(HookErrorCodes.MissingHookEventName);
            }

            if (!string.Equals(eventName, SupportedEventName, StringComparison.Ordinal))
            {
                return Failure(HookErrorCodes.UnsupportedHookEvent);
            }

            if (!TryOptionalString(root, "session_id", out var sessionId)
                || !TryOptionalString(root, "turn_id", out var turnId)
                || !TryOptionalString(root, "tool_use_id", out var toolUseId)
                || !TryOptionalString(root, "permission_mode", out var permissionMode)
                || !TryOptionalString(root, "tool_name", out var toolName))
            {
                return Failure(HookErrorCodes.InvalidJson);
            }

            return new CodexPostToolUseParseResult(
                true,
                new CodexPostToolUsePayload
                {
                    HookEventName = eventName,
                    SessionId = sessionId,
                    TurnId = turnId,
                    ToolUseId = toolUseId,
                    PermissionMode = permissionMode,
                    ToolName = toolName,
                },
                null);
        }
        catch (JsonException)
        {
            return Failure(HookErrorCodes.InvalidJson);
        }
    }

    private static bool TryOptionalString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static CodexPostToolUseParseResult Failure(string code) =>
        new(false, null, code);
}
