using System.Globalization;

namespace AgentBell.Localization;

/// <summary>Represents the supported user-selected application languages.</summary>
public enum AppLanguage
{
    /// <summary>Uses Simplified Chinese only for an exact zh-CN system UI culture.</summary>
    System,

    /// <summary>Forces English.</summary>
    English,

    /// <summary>Forces Simplified Chinese.</summary>
    ChineseSimplified,
}

/// <summary>Defines stable persisted language values and culture resolution.</summary>
public static class AppLanguageValues
{
    /// <summary>The persisted system-following value.</summary>
    public const string System = "system";

    /// <summary>The persisted English value.</summary>
    public const string English = "en-US";

    /// <summary>The persisted Simplified Chinese value.</summary>
    public const string ChineseSimplified = "zh-CN";

    /// <summary>Parses a persisted value, falling back to system for unknown values.</summary>
    public static AppLanguage Parse(string? value) => value switch
    {
        English => AppLanguage.English,
        ChineseSimplified => AppLanguage.ChineseSimplified,
        _ => AppLanguage.System,
    };

    /// <summary>Returns the stable persisted value.</summary>
    public static string ToPersistedValue(AppLanguage language) => language switch
    {
        AppLanguage.English => English,
        AppLanguage.ChineseSimplified => ChineseSimplified,
        _ => System,
    };

    /// <summary>Normalizes an external value to one of the three supported values.</summary>
    public static string Normalize(string? value) => ToPersistedValue(Parse(value));

    /// <summary>Resolves a selected language against the supplied system UI culture.</summary>
    public static CultureInfo ResolveCulture(AppLanguage language, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        return language switch
        {
            AppLanguage.ChineseSimplified => CultureInfo.GetCultureInfo(ChineseSimplified),
            AppLanguage.English => CultureInfo.GetCultureInfo(English),
            _ when string.Equals(
                systemUiCulture.Name,
                ChineseSimplified,
                StringComparison.OrdinalIgnoreCase) => CultureInfo.GetCultureInfo(ChineseSimplified),
            _ => CultureInfo.GetCultureInfo(English),
        };
    }
}
