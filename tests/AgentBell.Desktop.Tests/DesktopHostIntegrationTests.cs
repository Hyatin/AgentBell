using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentBell.Hook;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AgentBell.Desktop.Tests;

public sealed class DesktopHostIntegrationTests
{
    [Fact]
    public async Task DesktopHost_FullHookChain_BindsOnlyIpv4LoopbackAndPreservesUnavailableBehavior()
    {
        const string Json = """
            {
              "hook_event_name":"Stop",
              "session_id":"integration-private-session",
              "turn_id":"integration-private-turn",
              "cwd":"C:\\Private\\IntegrationProject",
              "last_assistant_message":"integration private response"
            }
            """;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Desktop-Integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var isolatedPort = GetIsolatedPort();
        var eventsPath = Path.Combine(directory, "events.json");
        var successLogPath = Path.Combine(directory, "hook-success.ndjson");
        var unavailableLogPath = Path.Combine(directory, "hook-unavailable.ndjson");

        try
        {
            await using (var application = DesktopHost.Build(
                new DesktopRuntimeOptions
                {
                    TestIsolationEnabled = true,
                    EventsFilePath = eventsPath,
                    LoopbackPort = isolatedPort,
                }))
            {
                await DesktopHost.InitializeAsync(application, CancellationToken.None);
                await application.StartAsync();

                var addresses = application.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()?
                    .Addresses;
                var address = Assert.Single(addresses ?? []);
                Assert.Equal($"http://127.0.0.1:{isolatedPort}", address);
                Assert.DoesNotContain("0.0.0.0", address, StringComparison.Ordinal);
                Assert.DoesNotContain("[::]", address, StringComparison.Ordinal);

                using var client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{isolatedPort}"),
                    Timeout = TimeSpan.FromSeconds(2),
                };
                using var getResponse = await client.GetAsync(DesktopHttpContract.EventsPath);
                Assert.Equal(405, (int)getResponse.StatusCode);

                var hookResult = await RunHookProcessAsync(
                    Json,
                    successLogPath,
                    directory,
                    isolatedPort);
                Assert.Equal(0, hookResult.ExitCode);
                Assert.Equal(HookApplication.StopHookContinueResponse, hookResult.StandardOutput);
                Assert.Equal(string.Empty, hookResult.StandardError);

                using var hookDiagnostic = JsonDocument.Parse(
                    Assert.Single(File.ReadAllLines(successLogPath)));
                Assert.Equal(
                    ForwardResult.SuccessCode,
                    hookDiagnostic.RootElement.GetProperty("result").GetString());
                Assert.Equal(202, hookDiagnostic.RootElement.GetProperty("httpStatus").GetInt32());

                using var eventDocument = JsonDocument.Parse(await File.ReadAllTextAsync(eventsPath));
                var persistedEvent = Assert.Single(
                    eventDocument.RootElement.EnumerateArray(),
                    item => string.Equals(
                        item.GetProperty("project").GetString(),
                        "IntegrationProject",
                        StringComparison.Ordinal));
                Assert.Equal("IntegrationProject", persistedEvent.GetProperty("project").GetString());
                var persistedText = eventDocument.RootElement.GetRawText();
                Assert.DoesNotContain("integration-private-session", persistedText, StringComparison.Ordinal);
                Assert.DoesNotContain("integration-private-turn", persistedText, StringComparison.Ordinal);
                Assert.DoesNotContain("C:\\Private\\IntegrationProject", persistedText, StringComparison.Ordinal);

                await application.StopAsync();
            }

            var unavailableResult = await RunHookProcessAsync(
                Json,
                unavailableLogPath,
                directory,
                isolatedPort);
            Assert.Equal(0, unavailableResult.ExitCode);
            Assert.Equal(HookApplication.StopHookContinueResponse, unavailableResult.StandardOutput);
            Assert.Equal(string.Empty, unavailableResult.StandardError);

            using var unavailableDiagnostic = JsonDocument.Parse(
                Assert.Single(File.ReadAllLines(unavailableLogPath)));
            var unavailableCode = unavailableDiagnostic.RootElement
                .GetProperty("result")
                .GetString();
            Assert.Contains(
                unavailableCode,
                new[] { HookErrorCodes.ForwardUnavailable, HookErrorCodes.ForwardTimeout });
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<HookProcessResult> RunHookProcessAsync(
        string stdin,
        string diagnosticPath,
        string isolationRoot,
        int isolatedPort)
    {
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
        startInfo.ArgumentList.Add(HookInputResolver.CodexStopHookOption);
        startInfo.Environment[DiagnosticLoggerFactory.EnabledEnvironmentVariable] = "1";
        startInfo.Environment[DiagnosticLoggerFactory.PathEnvironmentVariable] = diagnosticPath;
        startInfo.Environment[HookEndpointResolver.TestModeEnvironmentVariable] = "1";
        startInfo.Environment[HookEndpointResolver.TestLoopbackPortEnvironmentVariable] =
            isolatedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var codexHome = Path.Combine(isolationRoot, "codex-home");
        var dataHome = Path.Combine(isolationRoot, "data-home");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(dataHome);
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment[DesktopRuntimeOptions.DataHomeEnvironmentVariable] = dataHome;

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        return new HookProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static int GetIsolatedPort()
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != DesktopHost.ListenPort && !LanPortRange.Contains(port))
            {
                return port;
            }
        }
    }

    private sealed record HookProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
