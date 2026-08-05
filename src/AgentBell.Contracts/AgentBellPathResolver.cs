namespace AgentBell.Contracts;

/// <summary>Resolves every per-user Windows product path from the LocalAppData Known Folder.</summary>
public sealed class AgentBellPathResolver
{
    /// <summary>The stable data directory name.</summary>
    public const string DataDirectoryName = "AgentBell";

    /// <summary>The stable per-user installation subdirectory.</summary>
    public static readonly string InstallDirectoryRelativePath = Path.Combine("Programs", "AgentBell");

    private readonly Func<Environment.SpecialFolder, string> _knownFolderReader;

    /// <summary>Initializes a production resolver or an injectable Known Folder resolver for tests.</summary>
    public AgentBellPathResolver(Func<Environment.SpecialFolder, string>? knownFolderReader = null)
    {
        _knownFolderReader = knownFolderReader ?? Environment.GetFolderPath;
    }

    /// <summary>Gets the canonical LocalAppData Known Folder without consulting LOCALAPPDATA.</summary>
    public string LocalApplicationDataDirectory => ResolveKnownFolder();

    /// <summary>Gets the canonical AgentBell data directory.</summary>
    public string DataDirectory => Path.Combine(LocalApplicationDataDirectory, DataDirectoryName);

    /// <summary>Gets the canonical per-user AgentBell installation directory.</summary>
    public string InstallDirectory =>
        Path.Combine(LocalApplicationDataDirectory, InstallDirectoryRelativePath);

    /// <summary>Gets the installed release APK path.</summary>
    public string AndroidApkPath => Path.Combine(
        InstallDirectory,
        "android",
        $"AgentBell-Android-{AgentBellProduct.InformationalVersion}.apk");

    /// <summary>Gets a validated executable path in the stable installation directory.</summary>
    public string GetInstalledExecutablePath(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        if (!string.Equals(Path.GetFileName(executableName), executableName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(executableName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A simple executable file name is required.", nameof(executableName));
        }

        return Path.Combine(InstallDirectory, executableName);
    }

    private string ResolveKnownFolder()
    {
        var path = _knownFolderReader(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Local application data is unavailable.");
        }

        return Path.GetFullPath(path);
    }
}
