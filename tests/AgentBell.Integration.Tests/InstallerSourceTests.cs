namespace AgentBell.Integration.Tests;

public sealed class InstallerSourceTests
{
    [Fact]
    public void Installer_ProvidesMatchingEnglishAndSimplifiedChineseCustomMessages()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "AgentBell.iss"));
        var englishMessages = ReadCustomMessages(source, "en.");
        var chineseMessages = ReadCustomMessages(source, "zhcn.");
        var english = englishMessages.Keys;
        var chinese = chineseMessages.Keys;

        Assert.Equal(english.Order(), chinese.Order());
        Assert.NotEmpty(english);
        foreach (var key in english)
        {
            Assert.Equal(
                ReadFormatPlaceholders(englishMessages[key]),
                ReadFormatPlaceholders(chineseMessages[key]));
        }
        Assert.Contains("Name: \"en\"; MessagesFile: \"compiler:Default.isl\"", source);
        Assert.Contains(
            "Name: \"zhcn\"; MessagesFile: \"Languages\\ChineseSimplified.isl\"",
            source);
        var chineseLanguageFile = Path.Combine(
            root,
            "installer",
            "Languages",
            "ChineseSimplified.isl");
        Assert.True(File.Exists(chineseLanguageFile));
        Assert.Contains(
            "Inno Setup version 6.5.0+ Chinese Simplified messages",
            File.ReadAllText(chineseLanguageFile),
            StringComparison.Ordinal);
        Assert.DoesNotContain("MsgBox(\n        '", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SuppressibleMsgBox(\n      '", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Caption := '", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_CapturesChildDiagnosticsAndPropagatesFailureExitCode()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "AgentBell.iss"));

        Assert.Contains("ExecAndCaptureOutput(", source, StringComparison.Ordinal);
        Assert.Contains("LogCapturedLines('stdout'", source, StringComparison.Ordinal);
        Assert.Contains("LogCapturedLines('stderr'", source, StringComparison.Ordinal);
        Assert.Contains("GetCustomSetupExitCode", source, StringComparison.Ordinal);
        Assert.Contains("IntegrationFailureExitCode := ResultCode", source, StringComparison.Ordinal);
        Assert.Contains(
            "IntegrationParameters('repair', CodexHome)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IntegrationParameters('verify', CodexHome)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseException('Codex 集成失败", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_UsesOneEnvironmentBasedCodexHomeResolverWithoutUserProfileConstant()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "AgentBell.iss"));
        var unsupportedUserProfileConstant = string.Concat("{", "userprofile", "}");

        Assert.DoesNotContain(unsupportedUserProfileConstant, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("function ResolveCodexHome(): String;", source, StringComparison.Ordinal);
        Assert.Contains("CodexHome := Trim(GetEnv('CODEX_HOME'));", source, StringComparison.Ordinal);
        Assert.Contains("UserProfile := Trim(GetEnv('USERPROFILE'));", source, StringComparison.Ordinal);
        Assert.Contains(
            "Unable to resolve Codex home: USERPROFILE is not available.",
            source,
            StringComparison.Ordinal);
        Assert.Contains("PathCombine(UserProfile, '.codex')", source, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(source, "IntegrationParameters('"));
        Assert.Contains(
            "IntegrationParameters('uninstall', UninstallCodexHome)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_CreatesControlsOnlyAfterProgressFormExists()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "AgentBell.iss"));
        var initializeStart = source.IndexOf(
            "function InitializeUninstall(): Boolean;",
            StringComparison.Ordinal);
        var progressStart = source.IndexOf(
            "procedure InitializeUninstallProgressForm;",
            StringComparison.Ordinal);
        var uninstallStepStart = source.IndexOf(
            "procedure CurUninstallStepChanged",
            StringComparison.Ordinal);

        Assert.True(initializeStart >= 0);
        Assert.True(progressStart > initializeStart);
        Assert.True(uninstallStepStart > progressStart);
        var initializeBody = source[initializeStart..progressStart];
        var progressBody = source[progressStart..uninstallStepStart];
        Assert.DoesNotContain("UninstallProgressForm.StatusLabel", initializeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TNewCheckBox.Create(UninstallProgressForm)", initializeBody, StringComparison.Ordinal);
        Assert.Contains("TNewCheckBox.Create(UninstallProgressForm)", progressBody, StringComparison.Ordinal);
        Assert.Contains("UninstallProgressForm.StatusLabel.Top", progressBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstaller_LogsOptionalCleanupAndAbortsOnlyOnUnexpectedCriticalException()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "installer", "AgentBell.iss"));

        Assert.Contains("UninstallLogging=yes", source, StringComparison.Ordinal);
        Assert.Contains(
            "IntegrationParameters('uninstall', UninstallCodexHome)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("optional integration cleanup failed", source, StringComparison.Ordinal);
        Assert.Contains("hooks.json exists:", source, StringComparison.Ordinal);
        Assert.Contains("backup candidate count:", source, StringComparison.Ordinal);
        Assert.Contains("procedure DeinitializeUninstall", source, StringComparison.Ordinal);
        Assert.Contains("AgentBell uninstall critical exception:", source, StringComparison.Ordinal);
        Assert.Contains("Abort;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Assigned(DeleteDataCheckBox) and DeleteDataCheckBox.Checked",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBuild_RunsIsolatedInstallerIntegrationGate()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "scripts", "build-release.ps1"));

        Assert.Contains("test-codex-installer-integration.ps1", source, StringComparison.Ordinal);
        Assert.Contains("[Version]'6.4.0'", source, StringComparison.Ordinal);
        Assert.Contains("-SetupPath $builtSetup", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerIntegrationTest_ExercisesEnglishAndSimplifiedChineseSetupLanguages()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "test-codex-installer-integration.ps1"));

        Assert.Contains("'/LANG=en'", source, StringComparison.Ordinal);
        Assert.Contains("'/LANG=zhcn'", source, StringComparison.Ordinal);
        Assert.Contains("stored-language uninstall", source, StringComparison.Ordinal);
        Assert.Contains("Assert-OnlyOtherHookRemains -HooksPath $chineseHooksPath", source);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "installer", "AgentBell.iss")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(expected, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += expected.Length;
        }

        return count;
    }

    private static Dictionary<string, string> ReadCustomMessages(string source, string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            Assert.True(separator > prefix.Length);
            Assert.False(string.IsNullOrWhiteSpace(line[(separator + 1)..]));
            Assert.True(result.TryAdd(
                line[prefix.Length..separator],
                line[(separator + 1)..]));
        }

        return result;
    }

    private static string[] ReadFormatPlaceholders(string message) =>
        System.Text.RegularExpressions.Regex.Matches(message, @"%(?:\d+|n)")
            .Select(match => match.Value)
            .ToArray();
}
