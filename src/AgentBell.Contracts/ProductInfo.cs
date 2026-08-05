using System.Reflection;

namespace AgentBell.Contracts;

/// <summary>Defines AgentBell product metadata shared by every Windows component.</summary>
public static class AgentBellProduct
{
    /// <summary>Gets the stable three-part product version from central build metadata.</summary>
    public static string ProductVersion { get; } =
        typeof(AgentBellProduct).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Gets the public prerelease version from central build metadata.</summary>
    public static string InformationalVersion { get; } = ResolveInformationalVersion();

    private static string ResolveInformationalVersion()
    {
        var informationalVersion = typeof(AgentBellProduct).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return ProductVersion;
        }

        var metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataSeparator >= 0
            ? informationalVersion[..metadataSeparator]
            : informationalVersion;
    }
}
