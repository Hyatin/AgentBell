using System.Text.Json;
using System.Security.Cryptography;
using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Maps PermissionRequest metadata to the content-free loopback contract.</summary>
public interface IPermissionRequestSanitizer
{
    /// <summary>Creates sanitized JSON that contains no raw Hook content or identifiers.</summary>
    PermissionRequestSanitizationResult Sanitize(CodexPermissionRequestPayload payload);
}

/// <summary>Contains the sanitized payload and its serialized loopback representation.</summary>
public sealed record PermissionRequestSanitizationResult(
    SanitizedActionRequiredEvent Event,
    string Json);

/// <summary>Default deterministic PermissionRequest sanitizer.</summary>
public sealed class PermissionRequestSanitizer : IPermissionRequestSanitizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public PermissionRequestSanitizationResult Sanitize(CodexPermissionRequestPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var sessionHash = IdentifierHash.Create(payload.SessionId);
        var turnHash = IdentifierHash.Create(payload.TurnId);
        var toolUseHash = IdentifierHash.Create(payload.ToolUseId);
        var toolCategory = PermissionToolCategoryMapper.Map(payload.ToolName);
        var eventFingerprint = sessionHash is not null
            || turnHash is not null
            || toolUseHash is not null
                ? IdentifierHash.CreateFingerprint(
                    string.Join(
                        '|',
                        SanitizedActionRequiredEvent.PermissionRequestEventType,
                        sessionHash ?? "missing-session",
                        turnHash ?? "missing-turn",
                        toolUseHash ?? "missing-tool-use",
                        toolCategory))
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        var sanitized = new SanitizedActionRequiredEvent
        {
            EventId = $"codex-action:{eventFingerprint}",
            SessionIdHash = sessionHash,
            TurnIdHash = turnHash,
            ToolUseIdHash = toolUseHash,
            Project = ExtractProject(payload.WorkingDirectory),
            ToolCategory = toolCategory,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        return new PermissionRequestSanitizationResult(
            sanitized,
            JsonSerializer.Serialize(sanitized, SerializerOptions));
    }

    private static string? ExtractProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return null;
        }

        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var result = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? null : result;
    }
}

/// <summary>Maps untrusted tool names to a small allow-list without retaining the source value.</summary>
public static class PermissionToolCategoryMapper
{
    /// <summary>Maps one tool name to a stable safe category.</summary>
    public static string Map(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return AgentToolCategories.Other;
        }

        var normalized = toolName.Trim();
        if (normalized.Equals("Bash", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Shell", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            return AgentToolCategories.Command;
        }

        if (normalized.Equals("apply_patch", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Write", StringComparison.OrdinalIgnoreCase))
        {
            return AgentToolCategories.FileChange;
        }

        if (normalized.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase))
        {
            return AgentToolCategories.ExternalTool;
        }

        if (normalized.Contains("computer", StringComparison.OrdinalIgnoreCase))
        {
            return AgentToolCategories.ComputerControl;
        }

        if (normalized.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return AgentToolCategories.NetworkAccess;
        }

        return AgentToolCategories.Other;
    }
}
