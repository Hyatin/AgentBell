using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentBell.Hook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentBell.Desktop.Tests;

public sealed class DesktopHostIntegrationTests
{
    private static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BusinessRequestTimeout = TimeSpan.FromSeconds(10);

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
        var eventsPath = Path.Combine(directory, "events.json");
        var successLogPath = Path.Combine(directory, "hook-success.ndjson");
        var unavailableLogPath = Path.Combine(directory, "hook-unavailable.ndjson");
        var boundPort = 0;

        try
        {
            await using (var application = DesktopHost.Build(
                new DesktopRuntimeOptions
                {
                    TestIsolationEnabled = true,
                    EventsFilePath = eventsPath,
                    LoopbackPort = 0,
                }))
            {
                await DesktopHost.InitializeAsync(application, CancellationToken.None);
                await StartApplicationAsync(application, HostStartupTimeout);

                var addresses = application.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()?
                    .Addresses;
                var address = Assert.Single(addresses ?? []);
                var listenerUri = new Uri(address);
                boundPort = listenerUri.Port;
                Assert.Equal(Uri.UriSchemeHttp, listenerUri.Scheme);
                Assert.Equal(IPAddress.Loopback.ToString(), listenerUri.Host);
                Assert.InRange(boundPort, 1, 65535);
                Assert.Equal($"http://127.0.0.1:{boundPort}", address);
                Assert.DoesNotContain("0.0.0.0", address, StringComparison.Ordinal);
                Assert.DoesNotContain("[::]", address, StringComparison.Ordinal);

                var endpoint = new Uri(listenerUri, DesktopHttpContract.EventsPath);
                var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
                await WaitUntilReadyAsync(endpoint, lifetime, ReadinessTimeout);

                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = listenerUri,
                    Timeout = BusinessRequestTimeout,
                };
                using var getResponse = await client.GetAsync(DesktopHttpContract.EventsPath);
                Assert.Equal(405, (int)getResponse.StatusCode);

                var hookResult = await RunHookProcessAsync(
                    Json,
                    successLogPath,
                    directory,
                    boundPort);
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

                using var stopTimeout = new CancellationTokenSource(BusinessRequestTimeout);
                await application.StopAsync(stopTimeout.Token);
            }

            Assert.InRange(boundPort, 1, 65535);
            var unavailableResult = await RunHookProcessAsync(
                Json,
                unavailableLogPath,
                directory,
                boundPort);
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

    private static async Task StartApplicationAsync(WebApplication application, TimeSpan timeout)
    {
        var target = new Uri($"http://127.0.0.1:0{DesktopHttpContract.EventsPath}");
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await application.StartAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                CreateHostDiagnostic(target, stopwatch.Elapsed, "startup_timeout", "none"));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                CreateHostDiagnostic(
                    target,
                    stopwatch.Elapsed,
                    "startup_failed",
                    exception.GetType().Name));
        }
    }

    private static async Task WaitUntilReadyAsync(
        Uri endpoint,
        IHostApplicationLifetime lifetime,
        TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ReadinessRequestTimeout,
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var stopwatch = Stopwatch.StartNew();
        var lastFailure = "none";

        while (stopwatch.Elapsed < timeout)
        {
            var hostState = GetHostState(lifetime);
            if (!string.Equals(hostState, "running", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    CreateHostDiagnostic(endpoint, stopwatch.Elapsed, hostState, lastFailure));
            }

            var remaining = timeout - stopwatch.Elapsed;
            using var attemptTimeout = new CancellationTokenSource(
                remaining < ReadinessRequestTimeout ? remaining : ReadinessRequestTimeout);
            try
            {
                using var response = await client.GetAsync(
                    endpoint,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptTimeout.Token);
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    return;
                }

                lastFailure = $"http_{(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested)
            {
                lastFailure = "request_timeout";
            }
            catch (HttpRequestException exception)
            {
                lastFailure = exception.GetType().Name;
            }

            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < ReadinessPollInterval ? remaining : ReadinessPollInterval);
        }

        throw new TimeoutException(
            CreateHostDiagnostic(endpoint, stopwatch.Elapsed, GetHostState(lifetime), lastFailure));
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

    private static string CreateHostDiagnostic(
        Uri endpoint,
        TimeSpan elapsed,
        string hostState,
        string lastFailure) =>
        $"Desktop Host readiness failed. Target={endpoint}; "
        + $"WaitedMs={elapsed.TotalMilliseconds:F0}; HostState={hostState}; "
        + $"LastFailure={lastFailure}.";

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

    private sealed record HookProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
