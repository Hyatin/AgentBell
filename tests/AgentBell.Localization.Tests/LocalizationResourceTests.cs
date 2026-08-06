using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentBell.Localization;

namespace AgentBell.Localization.Tests;

public sealed partial class LocalizationResourceTests
{
    [Fact]
    public void ResourceFiles_HaveIdenticalNonEmptyKeysAndPlaceholders()
    {
        var root = FindRepositoryRoot();
        var english = ReadResources(Path.Combine(
            root,
            "src",
            "AgentBell.Localization",
            "Resources",
            "Strings.resx"));
        var chinese = ReadResources(Path.Combine(
            root,
            "src",
            "AgentBell.Localization",
            "Resources",
            "Strings.zh-CN.resx"));

        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        Assert.All(english.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.All(chinese.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        foreach (var key in english.Keys)
        {
            Assert.Equal(
                PlaceholderPattern().Matches(english[key]).Select(match => match.Value),
                PlaceholderPattern().Matches(chinese[key]).Select(match => match.Value));
        }
    }

    [Theory]
    [InlineData("system", "zh-CN", "zh-CN")]
    [InlineData("system", "en-US", "en-US")]
    [InlineData("system", "zh-TW", "en-US")]
    [InlineData("system", "zh-HK", "en-US")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("en-US", "zh-CN", "en-US")]
    [InlineData("invalid", "zh-CN", "zh-CN")]
    public void LanguageResolution_FollowsOnlyExactZhCnAndHonorsOverrides(
        string preference,
        string systemCulture,
        string expected)
    {
        var language = AppLanguageValues.Parse(preference);
        var culture = AppLanguageValues.ResolveCulture(
            language,
            CultureInfo.GetCultureInfo(systemCulture));

        Assert.Equal(expected, culture.Name);
        Assert.Equal(
            preference is "en-US" or "zh-CN" ? preference : "system",
            AppLanguageValues.ToPersistedValue(language));
    }

    [Fact]
    public void Localizer_UsesEnglishFallbackAndNeverDisplaysMissingKey()
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var localizer = new ResourceAppLocalizer(() => culture);

        Assert.Equal("Save", localizer.Get("Common_Save"));
        Assert.Equal("Text unavailable", localizer.Get("This_Key_Does_Not_Exist"));
        Assert.NotEqual("This_Key_Does_Not_Exist", localizer.Get("This_Key_Does_Not_Exist"));
        Assert.Throws<MissingManifestResourceException>(() =>
            new ResourceAppLocalizer(() => culture, throwOnMissingKey: true)
                .Get("This_Key_Does_Not_Exist"));
    }

    [Fact]
    public void PreferenceReader_RestoresLanguageAndInvalidValueFallsBackToSystem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, "{\"language\":\"zh-CN\",\"deviceId\":\"not-read\"}", new UTF8Encoding(false));
        Assert.Equal("zh-CN", AppLanguagePreferenceReader.Read(path));

        File.WriteAllText(path, "{\"language\":\"unsupported\"}", new UTF8Encoding(false));
        Assert.Equal("system", AppLanguagePreferenceReader.Read(path));
        var invalid = AppLanguagePreferenceReader.ReadWithStatus(path);
        Assert.Equal("system", invalid.Value);
        Assert.True(invalid.UsedInvalidValueFallback);

        File.WriteAllText(path, "{invalid", new UTF8Encoding(false));
        Assert.Equal("system", AppLanguagePreferenceReader.Read(path));
    }

    [Fact]
    public void LanguageService_UpdatesCurrentAndFutureUiCulture()
    {
        var previous = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var service = new AppLanguageService(
                "system",
                () => CultureInfo.GetCultureInfo("zh-CN"));
            Assert.Equal("zh-CN", service.EffectiveCulture.Name);
            Assert.Equal("保存", service.Localizer.Get("Common_Save"));

            service.SetLanguage(AppLanguage.English);
            Assert.Equal("en-US", service.EffectiveCulture.Name);
            Assert.Equal("Save", service.Localizer.Get("Common_Save"));
            Assert.Equal("en-US", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previous;
        }
    }

    private static Dictionary<string, string> ReadResources(string path)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var data = document.Root!.Elements("data").ToArray();
        Assert.Equal(data.Length, data.Select(item => item.Attribute("name")!.Value).Distinct().Count());
        return data.ToDictionary(
            item => item.Attribute("name")!.Value,
            item => item.Element("value")?.Value ?? string.Empty,
            StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AgentBell.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    [GeneratedRegex(@"\{\d+(?:[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentBell-Localization-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test-only cleanup.
            }
        }
    }
}
