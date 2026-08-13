using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBell.Contracts;

/// <summary>Identifies an agent provider with a stable, machine-readable value.</summary>
[JsonConverter(typeof(ProviderIdJsonConverter))]
public readonly record struct ProviderId
{
    private readonly string? _value;

    /// <summary>Initializes a provider identifier from its canonical value.</summary>
    /// <param name="value">The canonical lowercase provider identifier.</param>
    public ProviderId(string value)
    {
        ContractValueValidation.ValidateCanonicalIdentifier(value, 32, nameof(value));
        _value = value;
    }

    /// <summary>Gets the canonical lowercase provider identifier.</summary>
    public string Value => _value
        ?? throw new InvalidOperationException("The provider identifier is uninitialized.");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Known provider identifiers without imposing a closed provider registry.</summary>
public static class ProviderIds
{
    /// <summary>The Codex provider identifier.</summary>
    public static ProviderId Codex { get; } = new("codex");

    /// <summary>The Claude Code provider identifier reserved for a future provider adapter.</summary>
    public static ProviderId ClaudeCode { get; } = new("claude-code");
}

/// <summary>Identifies a provider-scoped source event with a canonical, bounded token.</summary>
[JsonConverter(typeof(SourceEventKindJsonConverter))]
public readonly record struct SourceEventKind
{
    private readonly string? _value;

    /// <summary>Initializes a source event kind from its canonical token.</summary>
    /// <param name="value">The provider-scoped lowercase event token.</param>
    public SourceEventKind(string value)
    {
        ContractValueValidation.ValidateCanonicalIdentifier(value, 64, nameof(value));
        _value = value;
    }

    /// <summary>Gets the canonical provider-scoped source event token.</summary>
    public string Value => _value
        ?? throw new InvalidOperationException("The source event kind is uninitialized.");

    /// <inheritdoc />
    public override string ToString() => Value;
}

internal sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
{
    public override bool HandleNull => true;

    public override ProviderId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A provider identifier must be a JSON string.");
        }

        try
        {
            return new ProviderId(reader.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The provider identifier is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProviderId value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

internal sealed class SourceEventKindJsonConverter : JsonConverter<SourceEventKind>
{
    public override bool HandleNull => true;

    public override SourceEventKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A source event kind must be a JSON string.");
        }

        try
        {
            return new SourceEventKind(reader.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The source event kind is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        SourceEventKind value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

internal static class ContractValueValidation
{
    public const int IdentifierHashLength = 12;
    public const int MaximumEventIdLength = 128;
    public const int MaximumProjectTextElements = 128;
    public const int MaximumSafeSummaryTextElements = 160;
    public const int MaximumStableKeyLength = 96;

    public static void ValidateCanonicalIdentifier(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 || value.Length > maximumLength || !IsLowerAsciiLetter(value[0]))
        {
            throw new ArgumentException("The value is not a canonical identifier.", parameterName);
        }

        var previousWasHyphen = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '-')
            {
                if (previousWasHyphen || index == value.Length - 1)
                {
                    throw new ArgumentException("The value is not a canonical identifier.", parameterName);
                }

                previousWasHyphen = true;
                continue;
            }

            if (!IsLowerAsciiLetter(character) && !char.IsAsciiDigit(character))
            {
                throw new ArgumentException("The value is not a canonical identifier.", parameterName);
            }

            previousWasHyphen = false;
        }
    }

    public static string? ValidateOptionalStableKey(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length is 0 or > MaximumStableKeyLength || !IsLowerAsciiLetter(value[0]))
        {
            throw new ArgumentException("The value is not a stable key.", parameterName);
        }

        var previousWasSeparator = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            var isSeparator = character is '.' or '-';
            if (isSeparator)
            {
                if (previousWasSeparator || index == value.Length - 1)
                {
                    throw new ArgumentException("The value is not a stable key.", parameterName);
                }

                previousWasSeparator = true;
                continue;
            }

            if (!IsLowerAsciiLetter(character) && !char.IsAsciiDigit(character))
            {
                throw new ArgumentException("The value is not a stable key.", parameterName);
            }

            previousWasSeparator = false;
        }

        return value;
    }

    public static string ValidateStableKey(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return ValidateOptionalStableKey(value, parameterName)!;
    }

    public static string ValidateClassificationRuleId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 or > MaximumStableKeyLength || !IsLowerAsciiLetter(value[0]))
        {
            throw new ArgumentException("The value is not a classification rule identifier.", parameterName);
        }

        var previousWasSeparator = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            var isSeparator = character is '.' or '-' or '_';
            if (isSeparator)
            {
                if (previousWasSeparator || index == value.Length - 1)
                {
                    throw new ArgumentException(
                        "The value is not a classification rule identifier.",
                        parameterName);
                }

                previousWasSeparator = true;
                continue;
            }

            if (!IsLowerAsciiLetter(character) && !char.IsAsciiDigit(character))
            {
                throw new ArgumentException(
                    "The value is not a classification rule identifier.",
                    parameterName);
            }

            previousWasSeparator = false;
        }

        return value;
    }

    public static string ValidateEventId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 or > MaximumEventIdLength
            || !IsLowerAsciiLetterOrDigit(value[0])
            || !IsLowerAsciiLetterOrDigit(value[^1]))
        {
            throw new ArgumentException("The event identifier is invalid.", parameterName);
        }

        foreach (var character in value)
        {
            if (!IsLowerAsciiLetterOrDigit(character) && character is not '-' and not ':')
            {
                throw new ArgumentException("The event identifier is invalid.", parameterName);
            }
        }

        return value;
    }

    public static string? ValidateOptionalHash(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length != IdentifierHashLength || value.Any(character =>
                !char.IsAsciiDigit(character) && (character is < 'a' or > 'f')))
        {
            throw new ArgumentException("The identifier hash must be 12 lowercase hexadecimal characters.", parameterName);
        }

        return value;
    }

    public static string? ValidateOptionalProject(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value is "." or ".."
            || new StringInfo(value).LengthInTextElements > MaximumProjectTextElements
            || value.Any(character => character is '/' or '\\' || IsUnsafeDisplayControl(character)))
        {
            throw new ArgumentException("The project must be a bounded, safe display component.", parameterName);
        }

        return value;
    }

    public static string? ValidateOptionalSafeSummary(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || new StringInfo(value).LengthInTextElements > MaximumSafeSummaryTextElements
            || value.Any(IsUnsafeDisplayControl))
        {
            throw new ArgumentException("The safe summary is not normalized or exceeds its bound.", parameterName);
        }

        return value;
    }

    public static string ValidateToolCategory(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is not AgentToolCategories.None
            and not AgentToolCategories.Command
            and not AgentToolCategories.FileChange
            and not AgentToolCategories.NetworkAccess
            and not AgentToolCategories.ExternalTool
            and not AgentToolCategories.ComputerControl
            and not AgentToolCategories.Other)
        {
            throw new ArgumentException("The tool category is not allow-listed.", parameterName);
        }

        return value;
    }

    public static T ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The enum value is undefined.");
        }

        return value;
    }

    private static bool IsLowerAsciiLetter(char value) => value is >= 'a' and <= 'z';

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        IsLowerAsciiLetter(value) || char.IsAsciiDigit(value);

    private static bool IsUnsafeDisplayControl(char value) =>
        char.IsControl(value)
        || char.GetUnicodeCategory(value) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
