using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Hook;
using Microsoft.AspNetCore.Builder;

namespace AgentBell.Integration.Tests;

public sealed class HookCommandExecutionTests
{
    [Fact]
    public async Task CommandWindows_SpacesAndChinese_OfflinePreservesContractAndFailsFast()
    {
        using var directory = CreateRunnableHookDirectory();
        var isolatedPort = GetIsolatedPort();
        var hookPath = Path.Combine(directory.Path, "AgentBell.Hook.exe");

        var result = await RunAsync(hookPath, UniqueStopJson(), directory.Path, isolatedPort);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HookApplication.StopHookContinueResponse, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2), result.Elapsed.ToString());
    }

    [Fact]
    public async Task CommandWindows_SpacesAndChinese_DesktopRunningReceivesHttp202()
    {
        using var directory = CreateRunnableHookDirectory(includeDesktop: true);
        var isolatedPort = GetIsolatedPort();
        var isolatedLanPort = GetIsolatedPort(isolatedPort);
        var hookPath = Path.Combine(directory.Path, "AgentBell.Hook.exe");
        var desktopPath = Path.Combine(directory.Path, "AgentBell.Desktop.exe");
        var endpoint = new Uri(
            $"http://127.0.0.1:{isolatedPort}{DesktopHttpContract.EventsPath}");
        var eventsPath = Path.Combine(directory.Path, "data-home", "events.json");
        var diagnosticPath = Path.Combine(directory.Path, "hook.ndjson");
        await using var desktop = DesktopProcessHarness.Start(
            desktopPath,
            directory.Path,
            directory.Path,
            isolatedPort,
            isolatedLanPort);

        Assert.Contains(" ", desktop.FileName, StringComparison.Ordinal);
        Assert.Contains("中文", desktop.FileName, StringComparison.Ordinal);
        Assert.Equal(directory.Path, desktop.WorkingDirectory);
        Assert.Equal(0, desktop.ArgumentCount);
        await desktop.WaitUntilReadyAsync(
            endpoint,
            DesktopProcessHarness.DefaultReadinessTimeout);

        var result = await RunAsync(
            hookPath,
            UniqueStopJson(),
            directory.Path,
            isolatedPort,
            diagnosticPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HookApplication.StopHookContinueResponse, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        var line = Assert.Single(await File.ReadAllLinesAsync(diagnosticPath));
        using var diagnostic = JsonDocument.Parse(line);
        Assert.Equal("success", diagnostic.RootElement.GetProperty("result").GetString());
        Assert.Equal(202, diagnostic.RootElement.GetProperty("httpStatus").GetInt32());
        var events = await new JsonEventStore(eventsPath)
            .LoadAsync(CancellationToken.None);
        Assert.Single(events.Events);
    }

    [Fact]
    public async Task DesktopProcess_RuntimeCredentialAuthenticatesAndWrongCredentialIsRejectedWithoutLeaks()
    {
        using var directory = CreateRunnableHookDirectory(includeDesktop: true);
        var loopbackPort = GetIsolatedPort();
        var lanPort = GetIsolatedPort(loopbackPort);
        var diagnosticPath = Path.Combine(directory.Path, "data-home", "logs", "desktop.ndjson");
        var loopbackEndpoint = new Uri(
            $"http://127.0.0.1:{loopbackPort}{DesktopHttpContract.EventsPath}");
        var statusEndpoint = new Uri($"http://127.0.0.1:{lanPort}{LanHost.StatusPath}");
        await using var desktop = DesktopProcessHarness.Start(
            Path.Combine(directory.Path, "AgentBell.Desktop.exe"),
            directory.Path,
            directory.Path,
            loopbackPort,
            lanPort,
            diagnosticPath: diagnosticPath);

        await desktop.WaitUntilReadyAsync(
            loopbackEndpoint,
            DesktopProcessHarness.DefaultReadinessTimeout);
        await desktop.WaitUntilLanReadyAsync(
            statusEndpoint,
            DesktopProcessHarness.DefaultReadinessTimeout);

        Assert.Equal(
            HttpStatusCode.OK,
            await desktop.SendStatusRequestAsync(statusEndpoint, useRuntimeCredential: true));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await desktop.SendStatusRequestAsync(statusEndpoint, useRuntimeCredential: false));
        var diagnostics = await File.ReadAllTextAsync(diagnosticPath);
        Assert.False(desktop.ContainsRuntimeCredential(diagnostics));
        Assert.DoesNotContain("Authorization", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandBuilder_UsesAbsoluteCmdAndQuotesPathWithoutPowerShellOrPathLookup()
    {
        var commands = new HookCommandBuilder().Build(
            "C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe");

        Assert.Equal(
            "\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-stop-hook",
            commands.Command);
        Assert.Equal(
            "cmd.exe /d /s /c \"\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-stop-hook\"",
            commands.CommandWindows);
        Assert.DoesNotContain("powershell", commands.CommandWindows, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DesktopProcess_ExitBeforeReady_ReportsBoundedSanitizedDiagnostics()
    {
        using var directory = CreateRunnableHookDirectory(includeDesktop: true);
        var blockedPort = GetIsolatedPort();
        var lanPort = GetIsolatedPort(blockedPort);
        using var blocker = new TcpListener(System.Net.IPAddress.Loopback, blockedPort);
        blocker.Start();
        var endpoint = new Uri(
            $"http://127.0.0.1:{blockedPort}{DesktopHttpContract.EventsPath}");
        var desktop = DesktopProcessHarness.Start(
            Path.Combine(directory.Path, "AgentBell.Desktop.exe"),
            directory.Path,
            directory.Path,
            blockedPort,
            lanPort);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                desktop.WaitUntilReadyAsync(endpoint, TimeSpan.FromSeconds(10)));

            Assert.Contains("exited before", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ExitCode=1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("FileName=", exception.Message, StringComparison.Ordinal);
            Assert.Contains("WorkingDirectory=", exception.Message, StringComparison.Ordinal);
            Assert.Contains("ArgumentCount=0", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"Endpoint={endpoint}", exception.Message, StringComparison.Ordinal);
            Assert.Contains("WaitedMs=", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Stdout=", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Stderr=", exception.Message, StringComparison.Ordinal);
            Assert.False(desktop.ContainsRuntimeCredential(exception.Message));
        }
        finally
        {
            await desktop.DisposeAsync();
        }

        Assert.True(desktop.HasExited);
    }

    [Fact]
    public async Task DesktopProcess_NeverReady_TimesOutAndLeavesNoOrphanProcess()
    {
        using var directory = CreateRunnableHookDirectory(includeDesktop: true);
        var desktopPort = GetIsolatedPort();
        var lanPort = GetIsolatedPort(desktopPort);
        var neverReadyPort = GetIsolatedPort(desktopPort, lanPort);
        using var neverReadyListener = new TcpListener(
            System.Net.IPAddress.Loopback,
            neverReadyPort);
        neverReadyListener.Start();
        var desktopEndpoint = new Uri(
            $"http://127.0.0.1:{desktopPort}{DesktopHttpContract.EventsPath}");
        var neverReadyEndpoint = new Uri(
            $"http://127.0.0.1:{neverReadyPort}{DesktopHttpContract.EventsPath}");
        var desktop = DesktopProcessHarness.Start(
            Path.Combine(directory.Path, "AgentBell.Desktop.exe"),
            directory.Path,
            directory.Path,
            desktopPort,
            lanPort);

        try
        {
            await desktop.WaitUntilReadyAsync(
                desktopEndpoint,
                DesktopProcessHarness.DefaultReadinessTimeout);
            var stopwatch = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                desktop.WaitUntilReadyAsync(neverReadyEndpoint, TimeSpan.FromMilliseconds(750)));
            stopwatch.Stop();

            Assert.Contains("bounded timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ExitCode=running", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), stopwatch.Elapsed.ToString());
            Assert.False(desktop.ContainsRuntimeCredential(exception.Message));
        }
        finally
        {
            await desktop.DisposeAsync();
        }

        Assert.True(desktop.HasExited);
    }

    [Fact]
    public async Task Automation_IsolatedFromProductionShapedRuntimeAndConnectedWebSocket()
    {
        using var productionDirectory = CreateEmptyDirectory("production");
        using var automationDirectory = CreateRunnableHookDirectory();
        var productionLoopbackPort = GetIsolatedPort();
        var productionLanPort = GetIsolatedPort(productionLoopbackPort);
        var automationPort = GetIsolatedPort(productionLoopbackPort, productionLanPort);
        var productionOptions = DesktopRuntimeOptions.CreateIsolatedTest(
            productionDirectory.Path,
            productionLoopbackPort,
            productionLanPort);
        await using var productionRuntime = new AgentBellRuntime(
            productionOptions,
            tokenProtector: new ReversibleTestTokenProtector());
        await productionRuntime.StartAsync(CancellationToken.None);

        using var productionClient = new ClientWebSocket();
        var pairingUrl = Assert.IsType<string>(productionRuntime.GetPairingUrl());
        var token = pairingUrl.Split("#token=", StringSplitOptions.None)[1]
            .Split('&', 2)[0];
        await productionClient.ConnectAsync(
            new Uri(
                $"ws://127.0.0.1:{productionLanPort}{AgentBellProtocol.WebSocketPath}"
                + $"?access_token={token}"),
            CancellationToken.None);
        using var hello = JsonDocument.Parse(await ReceiveTextAsync(productionClient));
        Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
        await SendTextAsync(productionClient, "{\"type\":\"resume\",\"lastSequence\":0}");

        var productionBefore = await productionRuntime.GetSnapshotAsync(CancellationToken.None);
        var productionEventsBefore = File.Exists(productionOptions.EventsFilePath)
            ? await File.ReadAllBytesAsync(productionOptions.EventsFilePath)
            : [];

        var automationOptions = new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            DataDirectoryPath = Path.Combine(automationDirectory.Path, "data-home"),
            EventsFilePath = Path.Combine(automationDirectory.Path, "data-home", "events.json"),
            DiagnosticLogPath = Path.Combine(automationDirectory.Path, "data-home", "logs", "desktop.ndjson"),
            LoopbackPort = automationPort,
        };
        await using var automationHost = DesktopHost.Build(automationOptions);
        await DesktopHost.InitializeAsync(automationHost, CancellationToken.None);
        await automationHost.StartAsync();
        try
        {
            var hookPath = Path.Combine(automationDirectory.Path, "AgentBell.Hook.exe");
            var result = await RunAsync(
                hookPath,
                UniqueStopJson(),
                automationDirectory.Path,
                automationPort);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(HookApplication.StopHookContinueResponse, result.StandardOutput);

            var automationEvents = await new JsonEventStore(automationOptions.EventsFilePath)
                .LoadAsync(CancellationToken.None);
            Assert.Single(automationEvents.Events);

            var productionAfter = await productionRuntime.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(productionBefore.EventCount, productionAfter.EventCount);
            Assert.Equal(productionBefore.LatestSequence, productionAfter.LatestSequence);
            var productionEventsAfter = File.Exists(productionOptions.EventsFilePath)
                ? await File.ReadAllBytesAsync(productionOptions.EventsFilePath)
                : [];
            Assert.Equal(productionEventsBefore, productionEventsAfter);

            using var noProductionEvent = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ReceiveTextAsync(productionClient, noProductionEvent.Token));
        }
        finally
        {
            await automationHost.StopAsync();
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string hookPath,
        string stdin,
        string isolationRoot,
        int isolatedPort,
        string? diagnosticPath = null)
    {
        Assert.True(File.Exists(hookPath));
        Assert.True(Directory.Exists(isolationRoot));
        var startInfo = new ProcessStartInfo
        {
            FileName = hookPath,
            WorkingDirectory = isolationRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(HookInputResolver.CodexStopHookOption);
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
        startInfo.Environment[DesktopRuntimeOptions.DataHomeEnvironmentVariable] = dataHome;
        if (diagnosticPath is not null)
        {
            startInfo.Environment[DiagnosticLoggerFactory.EnabledEnvironmentVariable] = "1";
            startInfo.Environment[DiagnosticLoggerFactory.PathEnvironmentVariable] = diagnosticPath;
        }
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        stopwatch.Stop();
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr,
            stopwatch.Elapsed);
    }

    private static RunnableDirectory CreateRunnableHookDirectory(bool includeDesktop = false)
    {
        var source = Path.GetDirectoryName(typeof(HookApplication).Assembly.Location)!;
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell 含 空格 中文 {Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        foreach (var pattern in new[]
                 {
                     "AgentBell.Hook*",
                     "AgentBell.Contracts.dll",
                     "AgentBell.Contracts.pdb",
                 })
        {
            foreach (var file in Directory.GetFiles(source, pattern))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }

        if (includeDesktop)
        {
            var desktopHost = Path.Combine(AppContext.BaseDirectory, "desktop-host");
            Assert.True(Directory.Exists(desktopHost), $"Desktop test host not found at {desktopHost}");
            foreach (var file in Directory.GetFiles(desktopHost))
            {
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetFileName(file)),
                    overwrite: true);
            }
        }

        Assert.True(File.Exists(Path.Combine(destination, "AgentBell.Hook.exe")));
        if (includeDesktop)
        {
            Assert.True(File.Exists(Path.Combine(destination, "AgentBell.Desktop.exe")));
        }

        return new RunnableDirectory(destination);
    }

    private static string UniqueStopJson() =>
        $"{{\"hook_event_name\":\"Stop\",\"session_id\":\"integration-session\",\"turn_id\":\"{Guid.NewGuid():N}\",\"last_assistant_message\":\"中文 🔔\"}}";

    private static int GetIsolatedPort(params int[] excludedPorts)
    {
        while (true)
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != DesktopHost.ListenPort
                && !LanPortRange.Contains(port)
                && !excludedPorts.Contains(port))
            {
                return port;
            }
        }
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Production-shaped socket closed unexpectedly.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static RunnableDirectory CreateEmptyDirectory(string label)
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell {label} isolation {Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        return new RunnableDirectory(destination);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan Elapsed);

    private sealed class ReversibleTestTokenProtector : IPairingTokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }

    private sealed class RunnableDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

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
