using System.Globalization;
using System.Resources;

namespace AgentBell.Localization;

/// <summary>Loads the shared default-English and zh-CN .resx resources.</summary>
public sealed class ResourceAppLocalizer : IAppLocalizer
{
    private const string MissingTextKey = "Localization_MissingText";
    private static readonly ResourceManager Resources = new(
        "AgentBell.Localization.Resources.Strings",
        typeof(ResourceAppLocalizer).Assembly);

    private readonly Func<CultureInfo> _cultureProvider;
    private readonly bool _throwOnMissingKey;

    /// <summary>Creates a localizer backed by the supplied effective-culture provider.</summary>
    public ResourceAppLocalizer(
        Func<CultureInfo> cultureProvider,
        bool throwOnMissingKey = false)
    {
        _cultureProvider = cultureProvider ?? throw new ArgumentNullException(nameof(cultureProvider));
        _throwOnMissingKey = throwOnMissingKey;
    }

    /// <inheritdoc />
    public CultureInfo Culture => _cultureProvider();

    /// <inheritdoc />
    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = Resources.GetString(key, Culture)
            ?? Resources.GetString(key, CultureInfo.GetCultureInfo(AppLanguageValues.English));
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (_throwOnMissingKey)
        {
            throw new MissingManifestResourceException($"Missing AgentBell UI resource: {key}");
        }

        return Resources.GetString(
            MissingTextKey,
            CultureInfo.GetCultureInfo(AppLanguageValues.English)) ?? "Text unavailable";
    }

    /// <inheritdoc />
    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);
}
