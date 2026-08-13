using System.Text.Json.Nodes;
using System.Text.Json;

namespace AgentBell.Integration.Tests;

public sealed class IntegrationServiceTests
{
    [Fact]
    public async Task Repair_CodexHomeUnsetAndHooksMissing_CreatesAndVerifiesFile()
    {
        using var directory = new TemporaryDirectory();
        var profile = Path.Combine(directory.Path, "profile 中文");
        var hookPath = CreateHook(directory.Path);
        var resolver = new CodexHomeResolver(_ => null, _ => profile);
        var service = new IntegrationService(hookPath, resolver);

        var result = await service.ExecuteAsync("repair", CancellationToken.None);
        var verify = await service.ExecuteAsync("verify", CancellationToken.None);

        var expectedHome = Path.Combine(profile, ".codex");
        Assert.True(result.Success, JsonSerializer.Serialize(result));
        Assert.True(result.Changed);
        Assert.Null(result.BackupPath);
        Assert.Equal("completed", result.Stage);
        Assert.Equal(Path.GetFullPath(expectedHome), result.CodexHomePath);
        Assert.True(File.Exists(Path.Combine(expectedHome, "hooks.json")));
        Assert.True(verify.Success, JsonSerializer.Serialize(verify));
        Assert.Equal("verified", verify.Code);
    }

    [Fact]
    public async Task Repair_CustomCodexHomeWithSpacesAndUnicode_UsesOneCanonicalPath()
    {
        using var directory = new TemporaryDirectory();
        var configuredHome = Path.Combine(directory.Path, "Codex Home 空格");
        var hookPath = CreateHook(Path.Combine(directory.Path, "Install Dir 空格"));
        var resolver = new CodexHomeResolver(
            name => name == CodexHomeResolver.CodexHomeEnvironmentVariable ? configuredHome : null,
            _ => throw new InvalidOperationException("The profile fallback must not be used."));
        var service = new IntegrationService(hookPath, resolver);

        var result = await service.ExecuteAsync("repair", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            WindowsPathCanonicalizer.Canonicalize(configuredHome),
            result.CodexHomePath,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(result.CodexHomePath!, "hooks.json"),
            result.HooksPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task Verify_MissingHookDefinition_ReturnsNonSuccessResult()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "codex");
        Directory.CreateDirectory(home);
        var service = new IntegrationService(
            CreateHook(directory.Path),
            new CodexHomeResolver(_ => home, _ => string.Empty));

        var result = await service.ExecuteAsync("verify", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("verification_failed", result.Code);
        Assert.Equal("verify", result.Stage);
    }

    [Fact]
    public void ResolveSiblingHookPath_UsesActualIntegrationDirectoryNotDefaultInstallDirectory()
    {
        using var directory = new TemporaryDirectory();
        var integrationDirectory = Path.Combine(directory.Path, "自定义 AgentBell 安装目录");
        Directory.CreateDirectory(integrationDirectory);

        var result = IntegrationProgram.ResolveSiblingHookPath(integrationDirectory);

        Assert.Equal(
            Path.Combine(integrationDirectory, "AgentBell.Hook.exe"),
            result,
            ignoreCase: true);
    }

    [Fact]
    public async Task Repair_LegalEmptyConfiguration_AddsExactlyOneHook()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "codex");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "hooks.json"), "{}");
        var service = new IntegrationService(
            CreateHook(directory.Path),
            new CodexHomeResolver(_ => home, _ => string.Empty));

        var result = await service.ExecuteAsync("repair", CancellationToken.None);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(result.HooksPath!))!.AsObject();

        Assert.True(result.Success);
        Assert.NotNull(result.BackupPath);
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public async Task Uninstall_MissingHooksFile_ReturnsSuccessfulIdempotentDiagnostics()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "missing hooks CODEX_HOME 中文");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(
            Path.Combine(home, "hooks.json.manual-backup-20260805-010203"),
            "{}");
        var service = new IntegrationService(
            CreateHook(directory.Path),
            new CodexHomeResolver(_ => home, _ => string.Empty));

        var result = await service.ExecuteAsync("uninstall", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Equal("hook_missing", result.Code);
        Assert.Equal("skipped_missing", result.Stage);
        Assert.False(result.HooksFileExistedBefore);
        Assert.Equal(1, result.BackupCandidateCount);
        Assert.Equal(Path.GetFullPath(home), result.CodexHomePath);
    }

    [Fact]
    public async Task IntegrationProgram_FreshRepair_UsesSiblingHookAndReturnsSafeJson()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "CLI CODEX_HOME 中文");
        var prior = Environment.GetEnvironmentVariable(CodexHomeResolver.CodexHomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CodexHomeResolver.CodexHomeEnvironmentVariable, home);
            using var output = new StringWriter();

            var exitCode = await IntegrationProgram.RunAsync(
                ["repair", "--json"],
                output,
                CancellationToken.None);
            using var result = JsonDocument.Parse(output.ToString());

            Assert.Equal(IntegrationExitCodes.Success, exitCode);
            Assert.True(result.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("installed", result.RootElement.GetProperty("code").GetString());
            Assert.Equal("completed", result.RootElement.GetProperty("stage").GetString());
            Assert.DoesNotContain("commandWindows", output.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(home, "hooks.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHomeResolver.CodexHomeEnvironmentVariable, prior);
        }
    }

    [Fact]
    public async Task IntegrationProgram_ExplicitCodexHomeWithSpacesAndUnicode_UsesExactPath()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "Explicit CODEX_HOME 空格");
        using var output = new StringWriter();

        var exitCode = await IntegrationProgram.RunAsync(
            ["repair", "--json", "--codex-home", home],
            output,
            CancellationToken.None);
        using var result = JsonDocument.Parse(output.ToString());

        Assert.Equal(IntegrationExitCodes.Success, exitCode);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(Path.GetFullPath(home), result.RootElement.GetProperty("codexHomePath").GetString());
        Assert.True(File.Exists(Path.Combine(home, "hooks.json")));
    }

    [Fact]
    public async Task IntegrationProgram_CodexHomeOptionWithoutPath_ReturnsInvalidArguments()
    {
        using var output = new StringWriter();

        var exitCode = await IntegrationProgram.RunAsync(
            ["uninstall", "--json", "--codex-home"],
            output,
            CancellationToken.None);

        Assert.Equal(IntegrationExitCodes.InvalidArguments, exitCode);
        Assert.Contains("invalid_arguments", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegrationProgram_07Operations_PreserveSafeJsonAndExitContracts()
    {
        const string Sentinel = "AGENTBELL_SECRET_SHOULD_NEVER_ESCAPE_7F3A";
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "CLI lifecycle 中文");
        var expectations = new[]
        {
            new CliExpectation("install", "installed", "Installed", "completed", true, 0, 3, true),
            new CliExpectation("status", "installed", "Installed", "completed", false, 3, 3, false),
            new CliExpectation("repair", "installed", "Installed", "completed", false, 3, 3, false),
            new CliExpectation("verify", "verified", "Installed", "completed", false, 3, 3, false),
            new CliExpectation("uninstall", "uninstalled", "Missing", "completed", true, 3, 0, false),
        };

        foreach (var expectation in expectations)
        {
            using var output = new StringWriter();
            var exitCode = await IntegrationProgram.RunAsync(
                [expectation.Operation, "--json", "--codex-home", home],
                output,
                CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;

            Assert.Equal(IntegrationExitCodes.Success, exitCode);
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(expectation.Changed, root.GetProperty("changed").GetBoolean());
            Assert.Equal(expectation.Code, root.GetProperty("code").GetString());
            Assert.Equal(expectation.State, root.GetProperty("state").GetString());
            Assert.Equal(expectation.Stage, root.GetProperty("stage").GetString());
            Assert.Equal(expectation.Before, root.GetProperty("agentBellHookCountBefore").GetInt32());
            Assert.Equal(expectation.After, root.GetProperty("agentBellHookCount").GetInt32());
            Assert.Equal(
                expectation.TrustReviewRequired,
                root.GetProperty("trustReviewRequired").GetBoolean());
            Assert.Equal(Path.GetFullPath(home), root.GetProperty("codexHomePath").GetString());
            Assert.Equal(
                Expected07ResultProperties,
                root.EnumerateObject().Select(item => item.Name).Order().ToArray());
            Assert.DoesNotContain("commandWindows", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Sentinel, output.ToString(), StringComparison.Ordinal);
            Assert.False(root.TryGetProperty("provider", out _));
            Assert.False(root.TryGetProperty("providerId", out _));
        }
    }

    [Fact]
    public async Task IntegrationProgram_Ambiguous07Ownership_FailsClosedWithManualReviewExit()
    {
        using var directory = new TemporaryDirectory();
        var home = Path.Combine(directory.Path, "ambiguous ownership");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(
            Path.Combine(home, "hooks.json"),
            """
            {"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"C:\\SyntheticA\\AgentBell\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]},
              {"hooks":[{"type":"command","command":"C:\\SyntheticB\\AgentBell\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]}
            ]}}
            """,
            new System.Text.UTF8Encoding(false));
        var before = await File.ReadAllBytesAsync(Path.Combine(home, "hooks.json"));
        using var output = new StringWriter();

        var exitCode = await IntegrationProgram.RunAsync(
            ["repair", "--json", "--codex-home", home],
            output,
            CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;

        Assert.Equal(IntegrationExitCodes.ManualReviewRequired, exitCode);
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("changed").GetBoolean());
        Assert.Equal("NeedsManualReview", root.GetProperty("state").GetString());
        Assert.Equal("manual_review_required", root.GetProperty("code").GetString());
        Assert.Equal("analyze", root.GetProperty("stage").GetString());
        Assert.Equal(2, root.GetProperty("agentBellHookCount").GetInt32());
        Assert.False(root.GetProperty("trustReviewRequired").GetBoolean());
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(home, "hooks.json")));
        Assert.Equal(
            Expected07ResultProperties,
            root.EnumerateObject().Select(item => item.Name).Order().ToArray());
    }

    [Fact]
    public void CodexHomeResolver_MissingEnvironmentAndUserProfile_ReturnsExplicitFailure()
    {
        var resolver = new CodexHomeResolver(_ => null, _ => string.Empty);

        var result = resolver.Resolve();

        Assert.False(result.IsAvailable);
        Assert.Equal("user_profile_unavailable", result.Code);
    }

    private static string CreateHook(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "AgentBell.Hook.exe");
        File.WriteAllBytes(path, [0x4d, 0x5a]);
        return path;
    }

    private static readonly string[] Expected07ResultProperties =
    [
        "agentBellHookCount",
        "agentBellHookCountBefore",
        "backupCandidateCount",
        "backupPath",
        "changed",
        "code",
        "codexHomePath",
        "completedStages",
        "hooksFileExistedBefore",
        "hooksPath",
        "rollbackAttempted",
        "rollbackSucceeded",
        "stage",
        "state",
        "success",
        "temporaryPath",
        "trustReviewRequired",
    ];

    private sealed record CliExpectation(
        string Operation,
        string Code,
        string State,
        string Stage,
        bool Changed,
        int Before,
        int After,
        bool TrustReviewRequired);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentBell-IntegrationService-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }
}
