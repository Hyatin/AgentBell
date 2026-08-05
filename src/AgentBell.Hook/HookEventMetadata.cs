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

    /// <summary>Creates source metadata for a Stop Hook invocation before payload parsing.</summary>
    public static HookEventMetadata ForStopHookInvocation() =>
        new() { EventType = "codex-stop" };

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
}
