using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Converts a validated Codex Stop payload into sanitized local event data.</summary>
public sealed class CodexEventTransformer
{
    /// <summary>The maximum number of Unicode text elements retained in a summary.</summary>
    public const int MaxSummaryTextElements = 160;

    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a transformer using the system clock unless another clock is supplied.</summary>
    public CodexEventTransformer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates a sanitized event candidate. The pipeline assigns its final sequence.</summary>
    public AgentEvent Transform(CodexStopHookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var threadIdHash = HashIdentifier(payload.SessionId);
        var turnIdHash = HashIdentifier(payload.TurnId);

        return new AgentEvent
        {
            EventId = CreateEventId(threadIdHash, turnIdHash),
            Agent = "codex",
            Status = "completed",
            Title = "Codex 已完成当前回合",
            Project = ExtractProject(payload.WorkingDirectory),
            Summary = CreateSummary(payload.LastAssistantMessage),
            ThreadIdHash = threadIdHash,
            TurnIdHash = turnIdHash,
            OccurredAt = _timeProvider.GetUtcNow(),
            Sequence = 0,
        };
    }

    /// <summary>Returns a deterministic 12-character SHA-256 reference without a random salt.</summary>
    public static string? HashIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>Extracts only the final directory segment from an untrusted path-like value.</summary>
    public static string? ExtractProject(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var trimmed = workingDirectory.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return null;
        }

        var separatorIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var segment = separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
            ? null
            : segment;
    }

    /// <summary>Normalizes whitespace and truncates without splitting a Unicode text element.</summary>
    public static string? CreateSummary(string? assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(assistantMessage))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(assistantMessage);
        if (normalized.Length == 0)
        {
            return null;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(normalized);
        var builder = new StringBuilder(Math.Min(normalized.Length, MaxSummaryTextElements * 2));
        var count = 0;
        while (count < MaxSummaryTextElements && enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());
            count++;
        }

        return builder.ToString();
    }

    private static string CreateEventId(string? threadIdHash, string? turnIdHash)
    {
        if (threadIdHash is not null && turnIdHash is not null)
        {
            return $"codex:{threadIdHash}:{turnIdHash}";
        }

        if (turnIdHash is not null)
        {
            return $"codex:{turnIdHash}";
        }

        return $"codex-local:{Guid.NewGuid():N}";
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var whitespacePending = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
