using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgentBell.Hook.Tests;

public sealed class StopHookProcessTests
{
    [Fact]
    public async Task StopHookProcess_NormalInput_ExitsZeroWithExactJsonStdoutAndEmptyStderr()
    {
        const string Json = """
            {"hook_event_name":"Stop","session_id":"process-session","turn_id":"process-turn"}
            """;

        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexStopHookOption],
            Json,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HookApplication.StopHookContinueResponse, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.DoesNotContain("process-session", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("process-turn", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"hook_event_name\":")]
    public async Task StopHookProcess_InvalidInput_StillExitsZeroWithExactJsonStdout(string stdin)
    {
        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexStopHookOption],
            stdin,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HookApplication.StopHookContinueResponse, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task PermissionRequestProcess_ValidInput_ExitsZeroAndIsCompletelySilent()
    {
        const string Json = """
            {
              "hook_event_name":"PermissionRequest",
              "session_id":"test-session-reference",
              "turn_id":"test-turn-reference",
              "tool_use_id":"test-tool-reference",
              "cwd":"C:\\TestRoot\\Project",
              "tool_name":"Bash",
              "tool_input":{"command":"<REDACTED_TEST_COMMAND>"}
            }
            """;

        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            Json,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"hook_event_name\":\"Stop\"}")]
    public async Task PermissionRequestProcess_InvalidInput_ExitsZeroAndIsCompletelySilent(
        string stdin)
    {
        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            stdin,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task PermissionRequestProcess_OversizedInput_ExitsZeroAndIsCompletelySilent()
    {
        var oversized = new string('x', HookInputResolver.MaxInputBytes + 1);

        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexPermissionRequestHookOption],
            oversized,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Theory]
    [InlineData("{\"hook_event_name\":\"PostToolUse\",\"turn_id\":\"turn\",\"tool_use_id\":\"tool\",\"tool_name\":\"Bash\"}")]
    [InlineData("{")]
    [InlineData("")]
    public async Task PostToolUseProcess_AlwaysExitsZeroAndIsCompletelySilent(string stdin)
    {
        var result = await RunHookProcessAsync(
            [HookInputResolver.CodexPostToolUseHookOption],
            stdin,
            diagnosticPath: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task StopHookProcess_SameIdsAcrossProcesses_ProducesStablePrivateHashes()
    {
        const string Json = """
            {
              "hook_event_name":"Stop",
              "session_id":"stop-session-stable",
              "turn_id":"stop-turn-stable",
              "cwd":"C:\\Private\\StopProject",
              "last_assistant_message":"private stop response"
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-Stop-{Guid.NewGuid():N}.ndjson");

        try
        {
            var first = await RunHookProcessAsync(
                [HookInputResolver.CodexStopHookOption],
                Json,
                path);
            var second = await RunHookProcessAsync(
                [HookInputResolver.CodexStopHookOption],
                Json,
                path);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(HookApplication.StopHookContinueResponse, first.StandardOutput);
            Assert.Equal(HookApplication.StopHookContinueResponse, second.StandardOutput);
            Assert.Equal(string.Empty, first.StandardError);
            Assert.Equal(string.Empty, second.StandardError);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var firstDocument = JsonDocument.Parse(lines[0]);
            using var secondDocument = JsonDocument.Parse(lines[1]);
            var firstRoot = firstDocument.RootElement;
            var secondRoot = secondDocument.RootElement;

            Assert.Equal("codex-stop", firstRoot.GetProperty("eventType").GetString());
            Assert.Equal("791be4077488", firstRoot.GetProperty("threadIdHash").GetString());
            Assert.Equal("401f953d3fd9", firstRoot.GetProperty("turnIdHash").GetString());
            Assert.Equal(
                firstRoot.GetProperty("threadIdHash").GetString(),
                secondRoot.GetProperty("threadIdHash").GetString());
            Assert.Equal(
                firstRoot.GetProperty("turnIdHash").GetString(),
                secondRoot.GetProperty("turnIdHash").GetString());

            var diagnosticText = string.Join('\n', lines);
            Assert.DoesNotContain(Json, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("stop-session-stable", diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("stop-turn-stable", diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Private\\StopProject", diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain("private stop response", diagnosticText, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task NotifyProcess_SingleJsonArgument_RemainsCompatible()
    {
        const string Json = """
            {"type":"agent-turn-complete","thread-id":"legacy-thread","turn-id":"legacy-turn"}
            """;
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-Notify-{Guid.NewGuid():N}.ndjson");

        try
        {
            var result = await RunHookProcessAsync(
                [Json],
                stdin: null,
                diagnosticPath: path);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);

            var line = Assert.Single(File.ReadAllLines(path));
            using var document = JsonDocument.Parse(line);
            Assert.Equal(
                "agent-turn-complete",
                document.RootElement.GetProperty("eventType").GetString());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task PayloadFileProcess_RemainsSilentOnStdoutAndStderr()
    {
        const string Json = "{\"type\":\"agent-turn-complete\"}";
        var path = Path.Combine(Path.GetTempPath(), $"AgentBell-Payload-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(path, Json, new UTF8Encoding(false));

            var result = await RunHookProcessAsync(
                [HookInputResolver.PayloadFileOption, path],
                stdin: null,
                diagnosticPath: null);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<ProcessResult> RunHookProcessAsync(
        IReadOnlyList<string> arguments,
        string? stdin,
        string? diagnosticPath)
    {
        var isolationRoot = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Hook-Isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolationRoot);
        var isolatedPort = GetIsolatedPort();
        var assemblyDirectory = Path.GetDirectoryName(typeof(HookApplication).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(assemblyDirectory));
        var hookPath = Path.Combine(assemblyDirectory, "AgentBell.Hook.exe");
        Assert.True(File.Exists(hookPath), $"Hook executable not found at {hookPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = hookPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove(DiagnosticLoggerFactory.EnabledEnvironmentVariable);
        startInfo.Environment.Remove(DiagnosticLoggerFactory.PathEnvironmentVariable);
        startInfo.Environment[HookEndpointResolver.TestModeEnvironmentVariable] = "1";
        startInfo.Environment[HookEndpointResolver.TestLoopbackPortEnvironmentVariable] =
            isolatedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var codexHome = Path.Combine(isolationRoot, "codex-home");
        var dataHome = Path.Combine(isolationRoot, "data-home");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(dataHome);
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["AGENTBELL_DATA_HOME"] = dataHome;
        if (diagnosticPath is not null)
        {
            startInfo.Environment[DiagnosticLoggerFactory.EnabledEnvironmentVariable] = "1";
            startInfo.Environment[DiagnosticLoggerFactory.PathEnvironmentVariable] = diagnosticPath;
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start());

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
            }

            process.StandardInput.Close();

            using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeoutSource.Token);

            return new ProcessResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask);
        }
        finally
        {
            try
            {
                Directory.Delete(isolationRoot, recursive: true);
            }
            catch
            {
                // Test isolation cleanup only.
            }
        }
    }

    private static int GetIsolatedPort()
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != 17863 && (port is < 17864 or > 17874))
            {
                return port;
            }
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
