using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace AgentBell.Integration.Tests;

public sealed class HooksJsonManagerTests
{
    [Fact]
    public async Task Install_FileMissing_CreatesOneOfEachManagedHook()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");

        var result = await Manager.InstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(CodexIntegrationState.Installed, result.State);
        Assert.Null(result.BackupPath);
        var root = ReadRoot(path);
        var handler = Assert.Single(Assert.Single(root["hooks"]!["Stop"]!.AsArray())!["hooks"]!.AsArray());
        Assert.Equal("command", handler!["type"]!.GetValue<string>());
        Assert.Equal(Commands.Command, handler["command"]!.GetValue<string>());
        Assert.Equal(Commands.CommandWindows, handler["commandWindows"]!.GetValue<string>());
        Assert.Equal(3, handler["timeout"]!.GetValue<int>());
        Assert.Equal(Commands.Stop.StatusMessage, handler["statusMessage"]!.GetValue<string>());
        var permissionHandler = Assert.Single(
            Assert.Single(root["hooks"]!["PermissionRequest"]!.AsArray())!["hooks"]!.AsArray());
        Assert.Equal(Commands.PermissionRequest.Command, permissionHandler!["command"]!.GetValue<string>());
        Assert.Equal(
            Commands.PermissionRequest.CommandWindows,
            permissionHandler["commandWindows"]!.GetValue<string>());
        Assert.Equal(3, permissionHandler["timeout"]!.GetValue<int>());
        Assert.Equal(
            Commands.PermissionRequest.StatusMessage,
            permissionHandler["statusMessage"]!.GetValue<string>());
        var postToolUseHandler = Assert.Single(
            Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray())!["hooks"]!.AsArray());
        Assert.Equal(Commands.PostToolUse.Command, postToolUseHandler!["command"]!.GetValue<string>());
        Assert.Equal(
            Commands.PostToolUse.CommandWindows,
            postToolUseHandler["commandWindows"]!.GetValue<string>());
        Assert.Equal(3, postToolUseHandler["timeout"]!.GetValue<int>());
        Assert.Equal(
            Commands.PostToolUse.StatusMessage,
            postToolUseHandler["statusMessage"]!.GetValue<string>());
        Assert.Equal(3, result.AgentBellHookCount);
    }

    [Fact]
    public async Task Install_ExistingOtherHooksAndTopLevelFields_PreservesTheirSemantics()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        const string Existing = """
            {
              "description":"keep me",
              "future":{"enabled":true},
              "hooks":{
                "Stop":[{"hooks":[{"type":"command","command":"other.exe"}]}],
                "PostToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"audit.exe"}]}]
              }
            }
            """;
        WriteUtf8(path, Existing);

        var result = await Manager.InstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        var root = ReadRoot(path);
        Assert.Equal("keep me", root["description"]!.GetValue<string>());
        Assert.True(root["future"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(2, root["hooks"]!["Stop"]!.AsArray().Count);
        Assert.Equal("audit.exe", root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Repair_AlreadyInstalled_IsIdempotentAndCreatesNoSecondBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        var first = await Manager.InstallAsync(path, Commands, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);

        var second = await Manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.True(first.Changed);
        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-backup-*"));
    }

    [Fact]
    public async Task Repair_OneDevelopmentHook_MigratesWithoutDuplicate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"G:\\Codex\\AgentBell\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook","timeout":3}]}]}}
            """);

        var result = await Manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.TrustReviewRequired);
        var status = await Manager.StatusAsync(path, Commands, CancellationToken.None);
        Assert.Equal(CodexIntegrationState.Installed, status.State);
        Assert.Equal(3, status.AgentBellHookCount);
    }

    [Fact]
    public async Task Repair_MultipleAgentBellCandidates_DoesNotModifyFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"G:\\a\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]},
              {"hooks":[{"type":"command","command":"G:\\b\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]}
            ]}}
            """);
        var before = await File.ReadAllBytesAsync(path);

        var result = await Manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CodexIntegrationState.NeedsManualReview, result.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-backup-*"));
    }

    [Fact]
    public async Task Repair_NonstandardAgentBellPath_RequiresManualReview()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"C:\\Other\\AgentBell.Hook.exe --codex-stop-hook"}]}]}}
            """);
        var before = await File.ReadAllBytesAsync(path);

        var result = await Manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.Equal(CodexIntegrationState.NeedsManualReview, result.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Repair_OtherProjectArtifactPath_DoesNotTreatFilenameAsKnownDevelopmentHook()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"C:\\OtherProject\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]}]}}
            """);
        var before = await File.ReadAllBytesAsync(path);

        var result = await Manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CodexIntegrationState.NeedsManualReview, result.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Install_InvalidJson_PreservesExactBytesAndCreatesNoBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        var bytes = Encoding.UTF8.GetBytes("{not valid");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Manager.InstallAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hooks_json_invalid", result.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-backup-*"));
    }

    [Fact]
    public async Task Install_ExistingFile_BackupIsByteForByteAndOutputIsUtf8WithoutBom()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        var original = Encoding.UTF8.GetBytes("{\"description\":\"中文\"}");
        await File.WriteAllBytesAsync(path, original);

        var result = await Manager.InstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(result.BackupPath));
        var output = await File.ReadAllBytesAsync(path);
        Assert.False(output.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-tmp-*"));
    }

    [Fact]
    public async Task Uninstall_RemovesOnlyAgentBellAndCleansOnlyItsEmptyGroup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"other":7,"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"other.exe"}]},
              {"hooks":[{"type":"command","command":"\"C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe\" --codex-stop-hook","commandWindows":"cmd.exe /d /s /c \"\"C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe\" --codex-stop-hook\"","timeout":3}]}
            ],"PermissionRequest":[
              {"hooks":[{"type":"command","command":"other-permission.exe"}]},
              {"hooks":[{"type":"command","command":"\"C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe\" --codex-permission-request-hook","commandWindows":"cmd.exe /d /s /c \"\"C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe\" --codex-permission-request-hook\"","timeout":3}]}
            ],"PostToolUse":[]}}
            """);

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        var root = ReadRoot(path);
        Assert.Equal(7, root["other"]!.GetValue<int>());
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
        Assert.Equal("other.exe", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Single(root["hooks"]!["PermissionRequest"]!.AsArray());
        Assert.Equal(
            "other-permission.exe",
            root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.NotNull(root["hooks"]!["PostToolUse"]);
    }

    [Fact]
    public async Task Uninstall_MissingHooksFile_IsSuccessfulIdempotentSkip()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Equal("hook_missing", result.Code);
        Assert.Equal("skipped_missing", result.Stage);
        Assert.False(result.HooksFileExistedBefore);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Uninstall_OnlyTimestampedBackupsExist_DoesNotRestoreOrDeleteThem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        var managedBackup = $"{path}.agentbell-backup-20260805-010203";
        var manualBackup = $"{path}.manual-backup-20260805-010204";
        WriteUtf8(managedBackup, "{}");
        WriteUtf8(manualBackup, "{}");

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hook_missing", result.Code);
        Assert.Equal(2, result.BackupCandidateCount);
        Assert.True(File.Exists(managedBackup));
        Assert.True(File.Exists(manualBackup));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Uninstall_InvalidJson_PreservesExactBytesAndReportsFailure()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        var bytes = Encoding.UTF8.GetBytes("{not-valid-json");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hooks_json_invalid", result.Code);
        Assert.True(result.HooksFileExistedBefore);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-backup-*"));
    }

    [Fact]
    public async Task Uninstall_DevelopmentM0Hook_RemovesOnlyKnownAgentBellDefinition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteUtf8(path, """
            {"hooks":{"Stop":[
              {"hooks":[{"type":"command","command":"other-tool.exe --stop"}]},
              {"hooks":[{"type":"command","command":"G:\\Repository\\AgentBell\\artifacts\\m0-hook\\AgentBell.Hook.exe --codex-stop-hook"}]}
            ]}}
            """);

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("uninstalled", result.Code);
        var root = ReadRoot(path);
        var group = Assert.Single(root["hooks"]!["Stop"]!.AsArray());
        Assert.Equal("other-tool.exe --stop", group!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uninstall_RepeatedCleanup_SecondRunIsSuccessfulSkipAndCreatesNoBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        await Manager.InstallAsync(path, Commands, CancellationToken.None);

        var first = await Manager.UninstallAsync(path, Commands, CancellationToken.None);
        var backupCount = Directory.GetFiles(directory.Path, "*.agentbell-backup-*").Length;
        var second = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal("uninstalled", first.Code);
        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal("hook_missing", second.Code);
        Assert.Equal(backupCount, Directory.GetFiles(directory.Path, "*.agentbell-backup-*").Length);
    }

    [Fact]
    public async Task Uninstall_HooksFileDisappearsAfterLoad_ReturnsSuccessfulSkipWithoutRecreatingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteRepairableHook(path);
        var manager = new HooksJsonManager(null, new DeleteBeforeUninstallWriteFileSystem(path));

        var result = await manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Equal("hook_missing", result.Code);
        Assert.Equal("skipped_missing", result.Stage);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Operations_NeverChangeConfigTomlOrNotify()
    {
        using var directory = new TemporaryDirectory();
        var hooks = Path.Combine(directory.Path, "hooks.json");
        var config = Path.Combine(directory.Path, "config.toml");
        var original = Encoding.UTF8.GetBytes("notify = [\"existing-runtime.exe\"]\nmodel = \"gpt\"\n");
        await File.WriteAllBytesAsync(config, original);

        await Manager.InstallAsync(hooks, Commands, CancellationToken.None);
        await Manager.RepairAsync(hooks, Commands, CancellationToken.None);
        await Manager.UninstallAsync(hooks, Commands, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(config));
    }

    [Fact]
    public void CodexHomeResolver_PrefersCodexHome()
    {
        using var directory = new TemporaryDirectory();
        var resolver = new CodexHomeResolver(
            name => name == CodexHomeResolver.CodexHomeEnvironmentVariable ? directory.Path : null,
            _ => "C:\\ignored");

        var result = resolver.Resolve();

        Assert.True(result.IsAvailable);
        Assert.Equal(Path.Combine(directory.Path, "hooks.json"), result.HooksPath);
    }

    [Fact]
    public void CodexHomeResolver_DefaultsToUserProfileDotCodex()
    {
        var resolver = new CodexHomeResolver(_ => null, _ => "C:\\Users\\Example");

        var result = resolver.Resolve();

        Assert.Equal("C:\\Users\\Example\\.codex\\hooks.json", result.HooksPath);
    }

    [Fact]
    public void CodexHomeResolver_DefaultUserProfileCanBeOnNonSystemDrive()
    {
        var resolver = new CodexHomeResolver(_ => null, _ => "E:\\Users\\Example");

        var result = resolver.Resolve();

        Assert.True(result.IsAvailable);
        Assert.Equal("E:\\Users\\Example\\.codex\\hooks.json", result.HooksPath);
    }

    [Fact]
    public async Task Repair_ReadOnlyHooksFile_PreservesOriginalBytesAndReportsWriteStage()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteRepairableHook(path);
        var original = await File.ReadAllBytesAsync(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var result = await Manager.RepairAsync(path, Commands, CancellationToken.None);

            Assert.False(result.Success);
            Assert.StartsWith("hooks_write_failed", result.Code, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.NotEqual("completed", result.Stage);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Repair_TemporaryWriteFailure_LeavesFormalFileUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteRepairableHook(path);
        var original = await File.ReadAllBytesAsync(path);
        var manager = new HooksJsonManager(null, new TemporaryWriteFailureFileSystem());

        var result = await manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hooks_write_failed", result.Code);
        Assert.Equal("temporary_write", result.Stage);
        Assert.False(result.RollbackAttempted);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.agentbell-tmp-*"));
    }

    [Fact]
    public async Task Repair_AtomicReplaceFailure_RestoresBackupBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "hooks.json");
        WriteRepairableHook(path);
        var original = await File.ReadAllBytesAsync(path);
        var manager = new HooksJsonManager(null, new DestructiveReplaceFailureFileSystem());

        var result = await manager.RepairAsync(path, Commands, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hooks_write_failed", result.Code);
        Assert.Equal("atomic_replace", result.Stage);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(result.BackupPath));
    }

    [Fact]
    public void WindowsPathCanonicalizer_ExistingLongAndShortPathsReferToSameFile()
    {
        using var directory = new TemporaryDirectory();
        var longDirectory = Path.Combine(directory.Path, "Long Directory 中文");
        Directory.CreateDirectory(longDirectory);
        var longPath = Path.Combine(longDirectory, "AgentBell.Hook.exe");
        File.WriteAllBytes(longPath, [0x4d, 0x5a]);
        var shortPath = TryGetShortPath(longPath);

        Assert.True(WindowsPathCanonicalizer.AreEquivalent(longPath, longPath));
        if (shortPath is not null)
        {
            Assert.True(WindowsPathCanonicalizer.AreEquivalent(longPath, shortPath));
        }
    }

    [Fact]
    public void CodexHomeResolver_ExpandsEnvironmentVariablesBeforeCanonicalizing()
    {
        using var directory = new TemporaryDirectory();
        const string VariableName = "AGENTBELL_CODEX_HOME_TEST_ROOT";
        var prior = Environment.GetEnvironmentVariable(VariableName);
        try
        {
            Environment.SetEnvironmentVariable(VariableName, directory.Path);
            var resolver = new CodexHomeResolver(
                name => name == CodexHomeResolver.CodexHomeEnvironmentVariable
                    ? $"%{VariableName}%\\codex home"
                    : null,
                _ => string.Empty);

            var result = resolver.Resolve();

            Assert.True(result.IsAvailable);
            Assert.Equal(
                Path.Combine(directory.Path, "codex home", "hooks.json"),
                result.HooksPath,
                ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, prior);
        }
    }

    private static HooksJsonManager Manager { get; } = new();

    private static HookCommands Commands { get; } = new HookCommandBuilder().Build(
        "C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe");

    private static JsonObject ReadRoot(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void WriteUtf8(string path, string value) =>
        File.WriteAllText(path, value, new UTF8Encoding(false));

    private static void WriteRepairableHook(string path)
    {
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = Commands.Command,
                                ["commandWindows"] = Commands.CommandWindows,
                                ["timeout"] = 1,
                            },
                        },
                    },
                },
            },
        };
        WriteUtf8(path, root.ToJsonString());
    }

    private static string? TryGetShortPath(string path)
    {
        var required = GetShortPathName(path, null, 0);
        if (required == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)required));
        var written = GetShortPathName(path, buffer, required);
        return written == 0 || written >= required ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder? shortPath, uint bufferLength);

    private sealed class TemporaryWriteFailureFileSystem : HooksFileSystem
    {
        internal override FileStream CreateWriteThroughFile(string path) =>
            throw new IOException("Injected temporary write failure.");
    }

    private sealed class DestructiveReplaceFailureFileSystem : HooksFileSystem
    {
        private bool _failureInjected;

        internal override void ReplaceFile(string source, string destination)
        {
            if (!_failureInjected)
            {
                _failureInjected = true;
                File.WriteAllText(destination, "corrupt", new UTF8Encoding(false));
                throw new IOException("Injected atomic replace failure.");
            }

            base.ReplaceFile(source, destination);
        }
    }

    private sealed class DeleteBeforeUninstallWriteFileSystem(string hooksPath) : HooksFileSystem
    {
        private int _hooksExistenceChecks;

        internal override bool FileExists(string path)
        {
            if (string.Equals(path, hooksPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _hooksExistenceChecks) == 3)
            {
                File.Delete(path);
                return false;
            }

            return base.FileExists(path);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentBell-Integration-{Guid.NewGuid():N}");
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
                // Test cleanup only.
            }
        }
    }
}
