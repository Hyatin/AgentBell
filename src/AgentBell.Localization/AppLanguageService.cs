using System.Globalization;
using System.Text.Json;

namespace AgentBell.Localization;

/// <summary>Owns the selected Windows language and applies its effective UI culture.</summary>
public sealed class AppLanguageService
{
    private readonly Func<CultureInfo> _systemUiCultureProvider;

    /// <summary>Creates a language service and immediately applies its culture.</summary>
    public AppLanguageService(
        string? persistedValue = null,
        Func<CultureInfo>? systemUiCultureProvider = null,
        bool throwOnMissingResource = false)
    {
        _systemUiCultureProvider = systemUiCultureProvider ?? (() => CultureInfo.InstalledUICulture);
        Current = AppLanguageValues.Parse(persistedValue);
        EffectiveCulture = AppLanguageValues.ResolveCulture(Current, _systemUiCultureProvider());
        Localizer = new ResourceAppLocalizer(() => EffectiveCulture, throwOnMissingResource);
        ApplyCulture();
    }

    /// <summary>Raised after the selected and effective language have changed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Gets the selected language.</summary>
    public AppLanguage Current { get; private set; }

    /// <summary>Gets the resolved culture used by UI resources.</summary>
    public CultureInfo EffectiveCulture { get; private set; }

    /// <summary>Gets the shared resource localizer.</summary>
    public IAppLocalizer Localizer { get; }

    /// <summary>Applies a supported language and refreshes current and future UI threads.</summary>
    public void SetLanguage(AppLanguage language)
    {
        Current = language;
        EffectiveCulture = AppLanguageValues.ResolveCulture(language, _systemUiCultureProvider());
        ApplyCulture();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCulture()
    {
        CultureInfo.DefaultThreadCurrentUICulture = EffectiveCulture;
        CultureInfo.CurrentUICulture = EffectiveCulture;
    }
}

/// <summary>Reads only the non-sensitive language preference from the existing config file.</summary>
public static class AppLanguagePreferenceReader
{
    private const long MaximumConfigBytes = 1024 * 1024;

    /// <summary>Returns a normalized preference without exposing or changing other configuration.</summary>
    public static string Read(string? configPath) => ReadWithStatus(configPath).Value;

    /// <summary>Returns the preference and whether an unsupported explicit value fell back.</summary>
    public static AppLanguagePreferenceResult ReadWithStatus(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new AppLanguagePreferenceResult(AppLanguageValues.System, false);
        }

        try
        {
            var info = new FileInfo(configPath);
            if (info.Length <= 0 || info.Length > MaximumConfigBytes)
            {
                return new AppLanguagePreferenceResult(AppLanguageValues.System, false);
            }

            using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("language", out var language))
            {
                return new AppLanguagePreferenceResult(AppLanguageValues.System, false);
            }

            var value = language.ValueKind == JsonValueKind.String
                ? language.GetString()
                : null;
            var valid = value is AppLanguageValues.System
                or AppLanguageValues.English
                or AppLanguageValues.ChineseSimplified;
            return new AppLanguagePreferenceResult(
                valid ? value! : AppLanguageValues.System,
                !valid);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return new AppLanguagePreferenceResult(AppLanguageValues.System, false);
        }
    }
}

/// <summary>Describes a sanitized persisted language read.</summary>
public sealed record AppLanguagePreferenceResult(string Value, bool UsedInvalidValueFallback);
