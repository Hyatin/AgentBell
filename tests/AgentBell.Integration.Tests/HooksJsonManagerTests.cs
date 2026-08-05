using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBell.Integration.Tests;

public sealed class HooksJsonManagerTests
{
    [Fact]
    public async Task Install_FileMissing_CreatesOneValidAgentBellStopHook()
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
        Assert.Equal(1, status.AgentBellHookCount);
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
            ],"PostToolUse":[]}}
            """);

        var result = await Manager.UninstallAsync(path, Commands, CancellationToken.None);

        Assert.True(result.Success);
        var root = ReadRoot(path);
        Assert.Equal(7, root["other"]!.GetValue<int>());
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
        Assert.Equal("other.exe", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.NotNull(root["hooks"]!["PostToolUse"]);
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

    private static HooksJsonManager Manager { get; } = new();

    private static HookCommands Commands { get; } = new HookCommandBuilder().Build(
        "C:\\Users\\Test\\AppData\\Local\\Programs\\AgentBell\\AgentBell.Hook.exe");

    private static JsonObject ReadRoot(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void WriteUtf8(string path, string value) =>
        File.WriteAllText(path, value, new UTF8Encoding(false));

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
