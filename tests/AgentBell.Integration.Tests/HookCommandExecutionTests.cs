using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Hook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentBell.Integration.Tests;

[Collection(ProcessIsolatedIntegrationCollection.Name)]
public sealed class HookCommandExecutionTests
{
    private static readonly TimeSpan ServiceStartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan EventCompletionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestHookForwardTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TestHookConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestHookProcessHardTimeout = TimeSpan.FromSeconds(8);

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
        var diagnostic = ReadHookDiagnostic(diagnosticPath);
        Assert.True(
            string.Equals(diagnostic.Result, ForwardResult.SuccessCode, StringComparison.Ordinal)
            && diagnostic.HttpStatusCode == (int)HttpStatusCode.Accepted,
            CreateDesktopProcessFailureDiagnostic(
                endpoint,
                desktop,
                result,
                diagnostic));
        var events = await new JsonEventStore(eventsPath)
            .LoadAsync(CancellationToken.None);
        Assert.Single(events.Events);
    }

    [Fact]
    public async Task PermissionAndPostToolUse_SpacesAndChinese_AreSanitizedAndRemainNonNotifyingWhenPolicyOff()
    {
        using var directory = CreateRunnableHookDirectory(includeDesktop: true);
        var isolatedPort = GetIsolatedPort();
        var isolatedLanPort = GetIsolatedPort(isolatedPort);
        var endpoint = new Uri(
            $"http://127.0.0.1:{isolatedPort}{DesktopHttpContract.EventsPath}");
        var eventsPath = Path.Combine(directory.Path, "data-home", "events.json");
        var diagnosticPath = Path.Combine(directory.Path, "permission-hook.ndjson");
        await using var desktop = DesktopProcessHarness.Start(
            Path.Combine(directory.Path, "AgentBell.Desktop.exe"),
            directory.Path,
            directory.Path,
            isolatedPort,
            isolatedLanPort);
        await desktop.WaitUntilReadyAsync(
            endpoint,
            DesktopProcessHarness.DefaultReadinessTimeout);

        const string SensitiveSentinel = "<REDACTED_TEST_COMMAND>";
        var result = await RunAsync(
            Path.Combine(directory.Path, "AgentBell.Hook.exe"),
            $$"""
              {
                "hook_event_name":"PermissionRequest",
                "session_id":"test-session-reference",
                "turn_id":"test-turn-reference",
                "tool_use_id":"test-tool-reference",
                "cwd":"C:\\Private\\AgentBell",
                "tool_name":"Bash",
                "tool_input":{"command":"{{SensitiveSentinel}}"}
              }
              """,
            directory.Path,
            isolatedPort,
            diagnosticPath,
            hookOption: HookInputResolver.CodexPermissionRequestHookOption);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(3), result.Elapsed.ToString());
        var diagnosticText = await File.ReadAllTextAsync(diagnosticPath);
        Assert.DoesNotContain(SensitiveSentinel, diagnosticText, StringComparison.Ordinal);
        using var diagnostic = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(diagnosticPath)));
        Assert.Equal("success", diagnostic.RootElement.GetProperty("result").GetString());
        Assert.Equal(202, diagnostic.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Empty((await new JsonEventStore(eventsPath).LoadAsync(CancellationToken.None)).Events);

        var postToolUse = await RunAsync(
            Path.Combine(directory.Path, "AgentBell.Hook.exe"),
            """
              {
                "hook_event_name":"PostToolUse",
                "session_id":"test-session-reference",
                "turn_id":"test-turn-reference",
                "tool_use_id":"test-tool-reference",
                "tool_name":"Bash",
                "tool_input":{"command":"<REDACTED_TEST_COMMAND>"},
                "tool_response":{"output":"<REDACTED_TEST_OUTPUT>"}
              }
              """,
            directory.Path,
            isolatedPort,
            diagnosticPath,
            hookOption: HookInputResolver.CodexPostToolUseHookOption);

        Assert.Equal(0, postToolUse.ExitCode);
        Assert.Equal(string.Empty, postToolUse.StandardOutput);
        Assert.Equal(string.Empty, postToolUse.StandardError);
        Assert.Empty((await new JsonEventStore(eventsPath).LoadAsync(CancellationToken.None)).Events);
        var allDiagnostics = await File.ReadAllTextAsync(diagnosticPath);
        Assert.DoesNotContain(SensitiveSentinel, allDiagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("<REDACTED_TEST_OUTPUT>", allDiagnostics, StringComparison.Ordinal);
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
        Assert.Equal(
            "\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-permission-request-hook",
            commands.PermissionRequest.Command);
        Assert.Equal(
            "cmd.exe /d /s /c \"\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-permission-request-hook\"",
            commands.PermissionRequest.CommandWindows);
        Assert.Equal(
            "\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-post-tool-use-hook",
            commands.PostToolUse.Command);
        Assert.Equal(
            "cmd.exe /d /s /c \"\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Hook.exe\" --codex-post-tool-use-hook\"",
            commands.PostToolUse.CommandWindows);
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
        var productionOptions = new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            DataDirectoryPath = productionDirectory.Path,
            EventsFilePath = Path.Combine(productionDirectory.Path, "events.json"),
            ConfigFilePath = Path.Combine(productionDirectory.Path, "config.json"),
            PairingQrCodePath = Path.Combine(productionDirectory.Path, "pairing", "pairing.png"),
            DiagnosticLogPath = Path.Combine(productionDirectory.Path, "logs", "desktop.ndjson"),
            LoopbackPort = 0,
            LanFirstPort = 0,
            LanLastPort = 0,
            LanAddressOverride = IPAddress.Loopback,
        };
        var productionDiagnostics = new SignalingDesktopDiagnosticLogger();
        await using var productionRuntime = new AgentBellRuntime(
            productionOptions,
            diagnosticLogger: productionDiagnostics,
            tokenProtector: new ReversibleTestTokenProtector());
        using (var startup = new CancellationTokenSource(ServiceStartupTimeout))
        {
            await productionRuntime.StartAsync(startup.Token);
        }

        var productionReady = await productionRuntime.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(RuntimeServiceStatus.Running, productionReady.LocalHookService);
        Assert.Equal(RuntimeServiceStatus.Available, productionReady.LanService);
        var selectedProductionLanPort = Assert.IsType<int>(productionReady.LanPort);
        Assert.InRange(selectedProductionLanPort, 1024, 65535);

        using var productionClient = new ClientWebSocket();
        var pairingUrl = Assert.IsType<string>(productionRuntime.GetPairingUrl());
        var token = pairingUrl.Split("#token=", StringSplitOptions.None)[1]
            .Split('&', 2)[0];
        var webSocketMessageTypes = new List<string>();
        using (var webSocketReady = new CancellationTokenSource(ServiceStartupTimeout))
        {
            await productionClient.ConnectAsync(
                new Uri(
                    $"ws://127.0.0.1:{selectedProductionLanPort}{AgentBellProtocol.WebSocketPath}"
                    + $"?access_token={token}"),
                webSocketReady.Token);
            using var hello = JsonDocument.Parse(
                await ReceiveTextAsync(productionClient, webSocketReady.Token));
            var helloType = hello.RootElement.GetProperty("type").GetString();
            Assert.Equal("hello", helloType);
            webSocketMessageTypes.Add(helloType!);
            await SendTextAsync(
                productionClient,
                "{\"type\":\"resume\",\"lastSequence\":0}",
                webSocketReady.Token);
            await productionDiagnostics.WaitForResumeAsync(webSocketReady.Token);
        }

        var connectedSnapshot = await productionRuntime.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(1, connectedSnapshot.WebSocketClientCount);

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
            LoopbackPort = 0,
        };
        var eventPublisher = new SignalingEventPublisher();
        await using var automationHost = DesktopHost.Build(automationOptions, eventPublisher);
        await DesktopHost.InitializeAsync(automationHost, CancellationToken.None);
        using (var startup = new CancellationTokenSource(ServiceStartupTimeout))
        {
            await automationHost.StartAsync(startup.Token);
        }

        var automationEndpoint = GetListenerEndpoint(automationHost);
        var automationPort = automationEndpoint.Port;
        var automationLifetime = automationHost.Services.GetRequiredService<IHostApplicationLifetime>();
        var automationReadyAt = await WaitUntilIngestionReadyAsync(
            automationEndpoint,
            automationLifetime,
            ServiceStartupTimeout);
        ProcessResult? result = null;
        HookDiagnosticSnapshot? hookDiagnostic = null;
        try
        {
            var hookPath = Path.Combine(automationDirectory.Path, "AgentBell.Hook.exe");
            var hookDiagnosticPath = Path.Combine(automationDirectory.Path, "hook.ndjson");
            result = await RunAsync(
                hookPath,
                UniqueStopJson(),
                automationDirectory.Path,
                automationPort,
                hookDiagnosticPath,
                TestHookForwardTimeout,
                TestHookConnectTimeout);
            hookDiagnostic = ReadHookDiagnostic(hookDiagnosticPath);
            Assert.True(
                result.ExitCode == 0
                && string.Equals(
                    result.StandardOutput,
                    HookApplication.StopHookContinueResponse,
                    StringComparison.Ordinal)
                && string.IsNullOrEmpty(result.StandardError)
                && string.Equals(
                    hookDiagnostic.Result,
                    ForwardResult.SuccessCode,
                    StringComparison.Ordinal)
                && hookDiagnostic.HttpStatusCode == (int)HttpStatusCode.Accepted,
                CreateAutomationFailureDiagnostic(
                    "hook_process",
                    automationEndpoint,
                    automationReadyAt,
                    automationLifetime,
                    productionClient,
                    productionBefore,
                    result,
                    hookDiagnostic,
                    eventPublisher.Events,
                    webSocketMessageTypes,
                    productionDiagnostics.Events));

            AgentEvent acceptedEvent;
            try
            {
                acceptedEvent = await eventPublisher.Event.WaitAsync(EventCompletionTimeout);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    CreateAutomationFailureDiagnostic(
                        "event_completion",
                        automationEndpoint,
                        automationReadyAt,
                        automationLifetime,
                        productionClient,
                        productionBefore,
                        result,
                        hookDiagnostic,
                        eventPublisher.Events,
                        webSocketMessageTypes,
                        productionDiagnostics.Events),
                    exception);
            }

            var automationEvents = await new JsonEventStore(automationOptions.EventsFilePath)
                .LoadAsync(CancellationToken.None);
            var persistedEvent = Assert.Single(automationEvents.Events);
            Assert.Equal(acceptedEvent.EventId, persistedEvent.EventId);

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
            try
            {
                using var shutdown = new CancellationTokenSource(ServiceStartupTimeout);
                await automationHost.StopAsync(shutdown.Token);
            }
            finally
            {
                await CloseWebSocketAsync(productionClient);
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string hookPath,
        string stdin,
        string isolationRoot,
        int isolatedPort,
        string? diagnosticPath = null,
        TimeSpan? forwardTimeout = null,
        TimeSpan? connectTimeout = null,
        string hookOption = HookInputResolver.CodexStopHookOption)
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
        startInfo.ArgumentList.Add(hookOption);
        startInfo.Environment.Remove(DiagnosticLoggerFactory.EnabledEnvironmentVariable);
        startInfo.Environment.Remove(DiagnosticLoggerFactory.PathEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestForwardTimeoutEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestConnectTimeoutEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestProcessTimeoutEnvironmentVariable);
        startInfo.Environment[HookEndpointResolver.TestModeEnvironmentVariable] = "1";
        startInfo.Environment[HookEndpointResolver.TestLoopbackPortEnvironmentVariable] =
            isolatedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[HookEndpointResolver.TestProcessTimeoutEnvironmentVariable] =
            checked((int)TestHookProcessHardTimeout.TotalMilliseconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        if (forwardTimeout is not null)
        {
            startInfo.Environment[HookEndpointResolver.TestForwardTimeoutEnvironmentVariable] =
                checked((int)forwardTimeout.Value.TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (connectTimeout is not null)
        {
            startInfo.Environment[HookEndpointResolver.TestConnectTimeoutEnvironmentVariable] =
                checked((int)connectTimeout.Value.TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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

    private static Uri GetListenerEndpoint(WebApplication application)
    {
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = Assert.Single(addresses ?? []);
        var listener = new Uri(address);
        Assert.Equal(Uri.UriSchemeHttp, listener.Scheme);
        Assert.Equal(IPAddress.Loopback.ToString(), listener.Host);
        Assert.InRange(listener.Port, 1, 65535);
        return new Uri(listener, DesktopHttpContract.EventsPath);
    }

    private static async Task<DateTimeOffset> WaitUntilIngestionReadyAsync(
        Uri endpoint,
        IHostApplicationLifetime lifetime,
        TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var overall = new CancellationTokenSource(timeout);
        var stopwatch = Stopwatch.StartNew();
        var lastResult = "not_attempted";
        while (!overall.IsCancellationRequested)
        {
            if (lifetime.ApplicationStopped.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Automation listener exited before readiness. TargetHost={endpoint.Host}; "
                    + $"TargetPort={endpoint.Port}; EndpointPath={endpoint.AbsolutePath}; "
                    + $"WaitedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            attempt.CancelAfter(ReadinessRequestTimeout);
            try
            {
                using var content = new StringContent(
                    "{\"hook_event_name\":\"ReadinessProbe\"}",
                    Encoding.UTF8,
                    "application/json");
                using var response = await client.PostAsync(endpoint, content, attempt.Token);
                lastResult = $"http_{(int)response.StatusCode}";
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (!overall.IsCancellationRequested)
            {
                lastResult = "request_timeout";
            }
            catch (HttpRequestException)
            {
                lastResult = "request_unavailable";
            }

            try
            {
                await Task.Delay(ReadinessPollInterval, overall.Token);
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException(
            $"Automation ingestion readiness timed out. TargetHost={endpoint.Host}; "
            + $"TargetPort={endpoint.Port}; EndpointPath={endpoint.AbsolutePath}; "
            + $"WaitedMs={stopwatch.Elapsed.TotalMilliseconds:F0}; "
            + $"HostState={GetHostState(lifetime)}; LastResult={lastResult}.");
    }

    private static HookDiagnosticSnapshot ReadHookDiagnostic(string path)
    {
        if (!File.Exists(path))
        {
            return new HookDiagnosticSnapshot("missing", null, 0, null, null);
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length != 1)
        {
            return new HookDiagnosticSnapshot($"line_count_{lines.Length}", null, 0, null, null);
        }

        using var document = JsonDocument.Parse(lines[0]);
        var root = document.RootElement;
        return new HookDiagnosticSnapshot(
            root.TryGetProperty("result", out var result)
                ? result.GetString() ?? "missing"
                : "missing",
            root.TryGetProperty("httpStatus", out var status)
                && status.ValueKind == JsonValueKind.Number
                    ? status.GetInt32()
                    : null,
            root.TryGetProperty("elapsedMs", out var elapsed)
                && elapsed.ValueKind == JsonValueKind.Number
                    ? elapsed.GetInt64()
                    : 0,
            root.TryGetProperty("failureStage", out var failureStage)
                ? failureStage.GetString()
                : null,
            root.TryGetProperty("exceptionType", out var exceptionType)
                ? exceptionType.GetString()
                : null);
    }

    private static string CreateDesktopProcessFailureDiagnostic(
        Uri endpoint,
        DesktopProcessHarness desktop,
        ProcessResult result,
        HookDiagnosticSnapshot diagnostic) =>
        $"Desktop Hook process failed. TargetHost={endpoint.Host}; TargetPort={endpoint.Port}; "
        + $"EndpointPath={endpoint.AbsolutePath}; Protocol={endpoint.Scheme}; "
        + $"DesktopAlive={!desktop.HasExited}; HookExitCode={result.ExitCode}; "
        + $"HookElapsedMs={result.Elapsed.TotalMilliseconds:F0}; "
        + $"HookStdout={SanitizeProcessText(result.StandardOutput)}; "
        + $"HookStderr={SanitizeProcessText(result.StandardError)}; "
        + $"ForwardResult={diagnostic.Result}; HttpStatus={diagnostic.HttpStatusCode?.ToString() ?? "none"}; "
        + $"ForwardElapsedMs={diagnostic.ElapsedMilliseconds}; "
        + $"FailureStage={diagnostic.FailureStage ?? "none"}; "
        + $"ExceptionType={diagnostic.ExceptionType ?? "none"}; "
        + $"TempPathHash={IdentifierHash.CreateFingerprint(Path.GetFullPath(desktop.WorkingDirectory))}.";

    private static string CreateAutomationFailureDiagnostic(
        string stage,
        Uri endpoint,
        DateTimeOffset readyAt,
        IHostApplicationLifetime automationLifetime,
        ClientWebSocket productionClient,
        AgentBellRuntimeSnapshot productionSnapshot,
        ProcessResult? process,
        HookDiagnosticSnapshot? hookDiagnostic,
        IReadOnlyList<AgentEvent> acceptedEvents,
        IReadOnlyList<string> clientMessageTypes,
        IReadOnlyList<DesktopDiagnosticEvent> runtimeDiagnostics)
    {
        var acceptedTypes = acceptedEvents
            .Select(item => $"{item.Agent}/{item.Status}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var runtimeMessageTypes = runtimeDiagnostics
            .Where(item => !string.IsNullOrWhiteSpace(item.MessageType))
            .Select(item => item.MessageType!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return $"Automation isolation failure. Stage={stage}; ServiceReady=true; "
            + $"ReadyAt={readyAt:O}; HostState={GetHostState(automationLifetime)}; "
            + $"TargetHost={endpoint.Host}; TargetPort={endpoint.Port}; "
            + $"EndpointPath={endpoint.AbsolutePath}; Protocol={endpoint.Scheme}; "
            + $"WebSocketState={productionClient.State}; "
            + $"ProductionLocalState={productionSnapshot.LocalHookService}; "
            + $"ProductionLanState={productionSnapshot.LanService}; "
            + $"ProductionRuntimeExited={productionSnapshot.LocalHookService != RuntimeServiceStatus.Running}; "
            + $"HookExitCode={process?.ExitCode.ToString() ?? "not_started"}; "
            + $"HookElapsedMs={process?.Elapsed.TotalMilliseconds.ToString("F0") ?? "none"}; "
            + $"HookStdout={SanitizeProcessText(process?.StandardOutput)}; "
            + $"HookStderr={SanitizeProcessText(process?.StandardError)}; "
            + $"ForwardResult={hookDiagnostic?.Result ?? "missing"}; "
            + $"ForwardHttpStatus={hookDiagnostic?.HttpStatusCode?.ToString() ?? "none"}; "
            + $"ForwardElapsedMs={hookDiagnostic?.ElapsedMilliseconds.ToString() ?? "none"}; "
            + $"AcceptedMessageCount={acceptedEvents.Count}; "
            + $"AcceptedMessageTypes={string.Join(',', acceptedTypes)}; "
            + $"ClientMessageTypes={string.Join(',', clientMessageTypes)}; "
            + $"RuntimeMessageTypes={string.Join(',', runtimeMessageTypes)}.";
    }

    private static string GetHostState(IHostApplicationLifetime lifetime)
    {
        if (lifetime.ApplicationStopped.IsCancellationRequested)
        {
            return "stopped";
        }

        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return "stopping";
        }

        return lifetime.ApplicationStarted.IsCancellationRequested ? "running" : "starting";
    }

    private static string SanitizeProcessText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 256 ? singleLine : singleLine[..256];
    }

    private static async Task CloseWebSocketAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test-complete",
                timeout.Token);
        }
        catch
        {
            socket.Abort();
        }
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

    private sealed record HookDiagnosticSnapshot(
        string Result,
        int? HttpStatusCode,
        long ElapsedMilliseconds,
        string? FailureStage,
        string? ExceptionType);

    private sealed class SignalingEventPublisher : IEventPublisher
    {
        private readonly ConcurrentQueue<AgentEvent> _events = new();
        private readonly TaskCompletionSource<AgentEvent> _event = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentEvent> Event => _event.Task;

        public IReadOnlyList<AgentEvent> Events => _events.ToArray();

        public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(agentEvent);
            _event.TrySetResult(agentEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SignalingDesktopDiagnosticLogger : IDesktopDiagnosticLogger
    {
        private readonly ConcurrentQueue<DesktopDiagnosticEvent> _events = new();
        private readonly TaskCompletionSource _resume = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DesktopDiagnosticEvent> Events => _events.ToArray();

        public void Record(DesktopDiagnosticEvent diagnosticEvent)
        {
            _events.Enqueue(diagnosticEvent);
            if (string.Equals(diagnosticEvent.MessageType, "resume", StringComparison.Ordinal)
                && string.Equals(diagnosticEvent.Result, "success", StringComparison.Ordinal))
            {
                _resume.TrySetResult();
            }
        }

        public Task WaitForResumeAsync(CancellationToken cancellationToken) =>
            _resume.Task.WaitAsync(cancellationToken);
    }

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
