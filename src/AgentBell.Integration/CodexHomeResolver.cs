namespace AgentBell.Integration;

/// <summary>Resolves the user-level Codex home without consulting project configuration.</summary>
public sealed class CodexHomeResolver
{
    /// <summary>The environment variable that overrides the default user Codex home.</summary>
    public const string CodexHomeEnvironmentVariable = "CODEX_HOME";

    private readonly Func<string, string?> _environmentReader;
    private readonly Func<Environment.SpecialFolder, string> _folderReader;
    private readonly Func<string, string> _pathCanonicalizer;

    /// <summary>Initializes a resolver with production or test environment readers.</summary>
    public CodexHomeResolver(
        Func<string, string?>? environmentReader = null,
        Func<Environment.SpecialFolder, string>? folderReader = null,
        Func<string, string>? pathCanonicalizer = null)
    {
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _folderReader = folderReader ?? Environment.GetFolderPath;
        _pathCanonicalizer = pathCanonicalizer ?? WindowsPathCanonicalizer.Canonicalize;
    }

    /// <summary>Resolves CODEX_HOME first, then the current profile's .codex directory.</summary>
    public CodexHomeResolution Resolve()
    {
        try
        {
            var configured = _environmentReader(CodexHomeEnvironmentVariable);
            string home;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                home = _pathCanonicalizer(configured);
            }
            else
            {
                var profile = _folderReader(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(profile))
                {
                    return CodexHomeResolution.Failure("user_profile_unavailable");
                }

                home = _pathCanonicalizer(Path.Combine(profile, ".codex"));
            }

            return CodexHomeResolution.Available(
                home,
                Path.Combine(home, "hooks.json"),
                Path.Combine(home, "config.toml"));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return CodexHomeResolution.Failure("codex_home_invalid");
        }
    }
}

/// <summary>Contains resolved user-level Codex paths or a stable failure code.</summary>
public sealed record CodexHomeResolution(
    bool IsAvailable,
    string? HomePath,
    string? HooksPath,
    string? ConfigPath,
    string Code)
{
    internal static CodexHomeResolution Available(
        string homePath,
        string hooksPath,
        string configPath) =>
        new(true, homePath, hooksPath, configPath, "success");

    internal static CodexHomeResolution Failure(string code) =>
        new(false, null, null, null, code);
}
