using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Locates and parses the supported Codex notify payload from command-line arguments.</summary>
public interface ICodexPayloadParser
{
    /// <summary>Parses an argument list without combining unrelated arguments.</summary>
    /// <param name="arguments">The command-line arguments supplied to the Hook process.</param>
    /// <returns>A stable parse result that never contains exception text.</returns>
    CodexPayloadParseResult Parse(IReadOnlyList<string> arguments);
}

/// <summary>Default <see cref="ICodexPayloadParser"/> implementation.</summary>
public sealed class CodexPayloadParser : ICodexPayloadParser
{
    private const string SupportedEventType = "agent-turn-complete";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
    };

    /// <inheritdoc />
    public CodexPayloadParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return Failure(HookErrorCodes.NoArguments);
        }

        var sawInvalidJson = false;
        var sawObjectWithoutType = false;

        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            var candidate = arguments[index];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                if (arguments.Count == 1 || LooksLikeJson(candidate))
                {
                    sawInvalidJson = true;
                }

                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("type", out var typeElement))
                {
                    sawObjectWithoutType = true;
                    continue;
                }

                if (typeElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(typeElement.GetString()))
                {
                    return Failure(HookErrorCodes.MissingType);
                }

                if (!string.Equals(typeElement.GetString(), SupportedEventType, StringComparison.Ordinal))
                {
                    return Failure(HookErrorCodes.UnsupportedType);
                }

                try
                {
                    var payload = JsonSerializer.Deserialize<CodexNotifyPayload>(candidate, SerializerOptions);
                    return payload is null
                        ? Failure(HookErrorCodes.InvalidJson)
                        : new CodexPayloadParseResult(true, payload, candidate, null);
                }
                catch (JsonException)
                {
                    return Failure(HookErrorCodes.InvalidJson);
                }
            }
        }

        if (sawObjectWithoutType)
        {
            return Failure(HookErrorCodes.MissingType);
        }

        return Failure(sawInvalidJson ? HookErrorCodes.InvalidJson : HookErrorCodes.JsonNotFound);
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return !trimmed.IsEmpty && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    private static CodexPayloadParseResult Failure(string errorCode) =>
        new(false, null, null, errorCode);
}

