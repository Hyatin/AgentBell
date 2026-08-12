using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Contains the common allow-listed event metadata used after source-specific parsing.</summary>
public sealed record HookEventMetadata
{
    /// <summary>Gets the normalized AgentBell event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Gets the source thread or session identifier. It must only be used to derive a hash.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Gets the source turn identifier. It must only be used to derive a hash.</summary>
    public string? TurnId { get; init; }

    /// <summary>Gets the optional working directory. It must never be logged in full.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the optional assistant message. It must never be logged.</summary>
    public string? LastAssistantMessage { get; init; }

    /// <summary>Gets the allow-listed tool category for PermissionRequest diagnostics.</summary>
    public string? ToolCategory { get; init; }

    /// <summary>Gets the sanitized event identifier, never a source identifier.</summary>
    public string? EventId { get; init; }

    /// <summary>Creates source metadata for a Stop Hook invocation before payload parsing.</summary>
    public static HookEventMetadata ForStopHookInvocation() =>
        new() { EventType = "codex-stop" };

    /// <summary>Creates source metadata before PermissionRequest parsing.</summary>
    public static HookEventMetadata ForPermissionHookInvocation() =>
        new() { EventType = SanitizedActionRequiredEvent.PermissionRequestEventType };

    /// <summary>Creates source metadata before PostToolUse parsing.</summary>
    public static HookEventMetadata ForPostToolUseHookInvocation() =>
        new() { EventType = SanitizedPostToolUseEvent.PostToolUseEventType };

    /// <summary>Maps a validated legacy Codex notify payload.</summary>
    public static HookEventMetadata FromNotify(CodexNotifyPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new HookEventMetadata
        {
            EventType = payload.Type ?? "agent-turn-complete",
            ThreadId = payload.ThreadId,
            TurnId = payload.TurnId,
            WorkingDirectory = payload.WorkingDirectory,
            LastAssistantMessage = payload.LastAssistantMessage,
        };
    }

    /// <summary>Maps a validated Codex Stop Hook payload.</summary>
    public static HookEventMetadata FromStopHook(CodexStopHookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new HookEventMetadata
        {
            EventType = "codex-stop",
            ThreadId = payload.SessionId,
            TurnId = payload.TurnId,
            WorkingDirectory = payload.WorkingDirectory,
            LastAssistantMessage = payload.LastAssistantMessage,
        };
    }

    /// <summary>Maps a PermissionRequest without retaining tool input or command content.</summary>
    public static HookEventMetadata FromPermissionRequest(
        CodexPermissionRequestPayload payload,
        SanitizedActionRequiredEvent sanitized)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(sanitized);

        return new HookEventMetadata
        {
            EventType = SanitizedActionRequiredEvent.PermissionRequestEventType,
            ThreadId = payload.SessionId,
            TurnId = payload.TurnId,
            WorkingDirectory = payload.WorkingDirectory,
            ToolCategory = sanitized.ToolCategory,
            EventId = sanitized.EventId,
        };
    }

    /// <summary>Maps PostToolUse metadata without retaining tool input or tool response.</summary>
    public static HookEventMetadata FromPostToolUse(
        CodexPostToolUsePayload payload,
        SanitizedPostToolUseEvent sanitized)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(sanitized);

        return new HookEventMetadata
        {
            EventType = SanitizedPostToolUseEvent.PostToolUseEventType,
            ThreadId = payload.SessionId,
            TurnId = payload.TurnId,
            ToolCategory = sanitized.ToolCategory,
        };
    }
}
