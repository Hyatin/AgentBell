using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Maps PostToolUse metadata to the content-free lifecycle correlation contract.</summary>
public interface IPostToolUseSanitizer
{
    /// <summary>Creates sanitized JSON that contains no raw tool content or source identifiers.</summary>
    PostToolUseSanitizationResult Sanitize(CodexPostToolUsePayload payload);
}

/// <summary>Contains a sanitized lifecycle payload and its serialized representation.</summary>
public sealed record PostToolUseSanitizationResult(
    SanitizedPostToolUseEvent Event,
    string Json);

/// <summary>Default PostToolUse sanitizer.</summary>
public sealed class PostToolUseSanitizer : IPostToolUseSanitizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public PostToolUseSanitizationResult Sanitize(CodexPostToolUsePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var sanitized = new SanitizedPostToolUseEvent
        {
            SessionIdHash = IdentifierHash.Create(payload.SessionId),
            TurnIdHash = IdentifierHash.Create(payload.TurnId),
            ToolUseIdHash = IdentifierHash.Create(payload.ToolUseId),
            ToolCategory = PermissionToolCategoryMapper.Map(payload.ToolName),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        return new PostToolUseSanitizationResult(
            sanitized,
            JsonSerializer.Serialize(sanitized, SerializerOptions));
    }
}
