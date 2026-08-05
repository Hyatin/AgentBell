using System.Text.Json.Serialization;

namespace AgentBell.Integration;

/// <summary>Defines the public state of the user-level Codex integration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodexIntegrationState>))]
public enum CodexIntegrationState
{
    /// <summary>The exact stable AgentBell command is installed once.</summary>
    Installed,

    /// <summary>No AgentBell Stop Hook is installed.</summary>
    Missing,

    /// <summary>A single safely repairable AgentBell Hook was found.</summary>
    NeedsRepair,

    /// <summary>The file or environment could not be safely interpreted.</summary>
    Unknown,

    /// <summary>Multiple or nonstandard AgentBell-like commands require manual review.</summary>
    NeedsManualReview,
}

/// <summary>Contains a machine-readable integration operation result without command text.</summary>
public sealed record CodexIntegrationResult
{
    /// <summary>Gets whether the requested operation completed safely.</summary>
    public required bool Success { get; init; }

    /// <summary>Gets whether hooks.json bytes were changed.</summary>
    public required bool Changed { get; init; }

    /// <summary>Gets the resulting integration state.</summary>
    public required CodexIntegrationState State { get; init; }

    /// <summary>Gets a stable error or result code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the resolved hooks.json path for user-directed repair.</summary>
    public string? HooksPath { get; init; }

    /// <summary>Gets the byte-for-byte backup path when a write occurred.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Gets the number of strictly identified AgentBell handlers.</summary>
    public int AgentBellHookCount { get; init; }

    /// <summary>Gets whether Codex may require review of a changed hook definition.</summary>
    public bool TrustReviewRequired { get; init; }
}

/// <summary>Provides stable process exit codes for installer and Tray automation.</summary>
public static class IntegrationExitCodes
{
    /// <summary>The operation succeeded, including an already-idempotent state.</summary>
    public const int Success = 0;

    /// <summary>The command line was invalid.</summary>
    public const int InvalidArguments = 2;

    /// <summary>Manual review is required before AgentBell can edit hooks.json.</summary>
    public const int ManualReviewRequired = 10;

    /// <summary>The hooks file is invalid and was preserved.</summary>
    public const int InvalidHooksJson = 11;

    /// <summary>A local filesystem or environment operation failed safely.</summary>
    public const int LocalOperationFailed = 12;
}
