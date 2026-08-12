using System.Text.Json.Serialization;

namespace AgentBell.Integration;

/// <summary>Defines the public state of the user-level Codex integration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodexIntegrationState>))]
public enum CodexIntegrationState
{
    /// <summary>Each exact stable AgentBell command Hook is installed once.</summary>
    Installed,

    /// <summary>No AgentBell managed Hook is installed.</summary>
    Missing,

    /// <summary>A safely repairable AgentBell Hook set was found.</summary>
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

    /// <summary>Gets the number of strictly identified AgentBell handlers before mutation.</summary>
    public int AgentBellHookCountBefore { get; init; }

    /// <summary>Gets whether hooks.json existed when the operation began.</summary>
    public bool HooksFileExistedBefore { get; init; }

    /// <summary>Gets the number of timestamped hooks.json backup candidates found before the operation.</summary>
    public int BackupCandidateCount { get; init; }

    /// <summary>Gets whether Codex may require review of a changed hook definition.</summary>
    public bool TrustReviewRequired { get; init; }

    /// <summary>Gets the canonical Codex home used by this operation.</summary>
    public string? CodexHomePath { get; init; }

    /// <summary>Gets the same-directory temporary file path used by an attempted write.</summary>
    public string? TemporaryPath { get; init; }

    /// <summary>Gets the last stable processing stage reached.</summary>
    public string Stage { get; init; } = "not_started";

    /// <summary>Gets the ordered, non-sensitive stages completed by the operation.</summary>
    public IReadOnlyList<string> CompletedStages { get; init; } = [];

    /// <summary>Gets whether restoration of the pre-operation file was required.</summary>
    public bool RollbackAttempted { get; init; }

    /// <summary>Gets whether a required rollback restored the previous bytes.</summary>
    public bool RollbackSucceeded { get; init; }
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
