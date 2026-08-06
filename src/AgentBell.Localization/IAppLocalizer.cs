using System.Globalization;

namespace AgentBell.Localization;

/// <summary>Provides UI strings for the current effective application culture.</summary>
public interface IAppLocalizer
{
    /// <summary>Gets the effective UI culture.</summary>
    CultureInfo Culture { get; }

    /// <summary>Gets a localized string by stable semantic key.</summary>
    string Get(string key);

    /// <summary>Formats a localized string using the effective UI culture.</summary>
    string Format(string key, params object?[] arguments);
}
