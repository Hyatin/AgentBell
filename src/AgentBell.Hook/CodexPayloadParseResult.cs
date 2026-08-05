using AgentBell.Contracts;

namespace AgentBell.Hook;

/// <summary>Contains the result of locating and parsing a Codex notify payload.</summary>
/// <param name="IsSuccess">Whether a supported payload was found.</param>
/// <param name="Payload">The parsed payload when successful.</param>
/// <param name="RawJson">The exact JSON argument when successful.</param>
/// <param name="ErrorCode">A stable error code when parsing was unsuccessful.</param>
public sealed record CodexPayloadParseResult(
    bool IsSuccess,
    CodexNotifyPayload? Payload,
    string? RawJson,
    string? ErrorCode);

