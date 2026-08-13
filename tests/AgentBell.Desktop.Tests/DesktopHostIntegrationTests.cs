using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentBell.Hook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentBell.Desktop.Tests;

[Collection(ProcessIsolatedDesktopCollection.Name)]
public sealed class DesktopHostIntegrationTests
{
    private static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadinessRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BusinessRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestHookForwardTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TestHookConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestHookProcessHardTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HookProcessStartupAndExitMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HookProcessTimeout =
        TestHookProcessHardTimeout + HookProcessStartupAndExitMargin;
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(2);

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
        var desktopDiagnostics = new CollectingDesktopDiagnosticLogger();
        var requestObserver = new HookRequestObserver();

        try
        {
            var runtimeOptions = new DesktopRuntimeOptions
            {
                TestIsolationEnabled = true,
                EventsFilePath = eventsPath,
                LoopbackPort = 0,
            };
            await using (var application = DesktopHost.Build(
                runtimeOptions,
                diagnosticLogger: desktopDiagnostics))
            {
                application.Use(requestObserver.ObserveAsync);
                Assert.Same(
                    runtimeOptions,
                    application.Services.GetRequiredService<DesktopRuntimeOptions>());
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
                var basicReadyAt = await WaitUntilReadyAsync(endpoint, lifetime, ReadinessTimeout);
                var fullChainReadyAt = await WaitUntilFullHookChainReadyAsync(
                    endpoint,
                    lifetime,
                    desktopDiagnostics,
                    requestObserver,
                    directory,
                    basicReadyAt,
                    ReadinessTimeout);

                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = listenerUri,
                    Timeout = BusinessRequestTimeout,
                };
                using var getResponse = await client.GetAsync(DesktopHttpContract.EventsPath);
                Assert.Equal(405, (int)getResponse.StatusCode);

                var observationCheckpoint = requestObserver.CreateCheckpoint();
                var hookResult = await RunHookProcessAsync(
                    Json,
                    successLogPath,
                    directory,
                    boundPort,
                    TestHookForwardTimeout,
                    HookProcessTimeout,
                    TestHookConnectTimeout);
                var hookDiagnostic = ReadHookDiagnostic(successLogPath);
                var expectedTurnHash = CodexEventTransformer.HashIdentifier("integration-private-turn")!;
                await WaitForDesktopObservationAsync(
                    requestObserver,
                    observationCheckpoint,
                    desktopDiagnostics,
                    expectedTurnHash,
                    ObservationTimeout);
                AssertHookSucceeded(
                    hookResult,
                    hookDiagnostic,
                    endpoint,
                    lifetime,
                    fullChainReadyAt,
                    requestObserver.GetObservationSince(observationCheckpoint),
                    FindDesktopDiagnostic(desktopDiagnostics, expectedTurnHash));

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
                boundPort,
                forwardTimeout: null,
                processTimeout: HookProcessTimeout);
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

    [Fact]
    public async Task FullChainReadiness_GetOnlyEndpoint_DoesNotReportHookReady()
    {
        var directory = CreateIsolatedDirectory("get-only");
        var desktopDiagnostics = new CollectingDesktopDiagnosticLogger();
        var requestObserver = new HookRequestObserver();
        var application = BuildGetOnlyHost(requestObserver);
        try
        {
            await StartApplicationAsync(application, HostStartupTimeout);
            var endpoint = GetBoundHookEndpoint(application);
            var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
            var basicReadyAt = await WaitUntilReadyAsync(endpoint, lifetime, ReadinessTimeout);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WaitUntilFullHookChainReadyAsync(
                    endpoint,
                    lifetime,
                    desktopDiagnostics,
                    requestObserver,
                    directory,
                    basicReadyAt,
                    HookProcessTimeout,
                    TimeSpan.FromMilliseconds(250)));

            Assert.Contains("Full Hook chain readiness failed", exception.Message, StringComparison.Ordinal);
            Assert.Contains("ForwardResult=forward_rejected", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await StopApplicationIfRunningAsync(application);
            await application.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task HookWithoutExplicitDynamicPort_FailsClosedInsteadOfUsingDefaultPort()
    {
        var directory = CreateIsolatedDirectory("missing-port");
        var desktopDiagnostics = new CollectingDesktopDiagnosticLogger();
        var requestObserver = new HookRequestObserver();
        var options = new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            EventsFilePath = Path.Combine(directory, "events.json"),
            LoopbackPort = 0,
        };
        var application = DesktopHost.Build(
            options,
            diagnosticLogger: desktopDiagnostics);
        application.Use(requestObserver.ObserveAsync);
        try
        {
            await DesktopHost.InitializeAsync(application, CancellationToken.None);
            await StartApplicationAsync(application, HostStartupTimeout);
            var endpoint = GetBoundHookEndpoint(application);
            var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
            await WaitUntilReadyAsync(endpoint, lifetime, ReadinessTimeout);
            var checkpoint = requestObserver.CreateCheckpoint();
            var diagnosticPath = Path.Combine(directory, "hook-missing-port.ndjson");

            var result = await RunHookProcessAsync(
                CreateProbePayload(out _),
                diagnosticPath,
                directory,
                isolatedPort: null,
                forwardTimeout: TimeSpan.FromMilliseconds(500),
                processTimeout: HookProcessTimeout);
            var diagnostic = ReadHookDiagnostic(diagnosticPath);

            Assert.Equal(1, result.TargetEndpoint.Port);
            Assert.NotEqual(endpoint, result.TargetEndpoint);
            Assert.True(
                string.Equals(
                    diagnostic.Result,
                    HookErrorCodes.ForwardUnavailable,
                    StringComparison.Ordinal),
                $"Expected fail-closed endpoint classification. "
                + $"ActualResult={diagnostic.Result}; "
                + $"FailureStage={diagnostic.FailureStage ?? "none"}; "
                + $"ExceptionType={diagnostic.ExceptionType ?? "none"}; "
                + $"ElapsedMs={diagnostic.ElapsedMilliseconds}.");
            Assert.Null(diagnostic.FailureStage);
            Assert.Null(diagnostic.ExceptionType);
            Assert.False(requestObserver.GetObservationSince(checkpoint).Received);
            Assert.DoesNotContain(
                desktopDiagnostics.Events,
                item => item.HttpStatusCode == StatusCodes.Status202Accepted);
        }
        finally
        {
            await StopApplicationIfRunningAsync(application);
            await application.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DynamicDesktopHosts_RunFullReadinessInParallelWithoutPortCollision()
    {
        var firstDirectory = CreateIsolatedDirectory("parallel-first");
        var secondDirectory = CreateIsolatedDirectory("parallel-second");
        var firstDiagnostics = new CollectingDesktopDiagnosticLogger();
        var secondDiagnostics = new CollectingDesktopDiagnosticLogger();
        var firstObserver = new HookRequestObserver();
        var secondObserver = new HookRequestObserver();
        var first = BuildDynamicDesktopHost(
            firstDirectory,
            firstDiagnostics,
            firstObserver);
        var second = BuildDynamicDesktopHost(
            secondDirectory,
            secondDiagnostics,
            secondObserver);
        try
        {
            await DesktopHost.InitializeAsync(first, CancellationToken.None);
            await DesktopHost.InitializeAsync(second, CancellationToken.None);
            await Task.WhenAll(
                StartApplicationAsync(first, HostStartupTimeout),
                StartApplicationAsync(second, HostStartupTimeout));
            var firstEndpoint = GetBoundHookEndpoint(first);
            var secondEndpoint = GetBoundHookEndpoint(second);
            Assert.NotEqual(firstEndpoint.Port, secondEndpoint.Port);
            Assert.Equal(IPAddress.Loopback.ToString(), firstEndpoint.Host);
            Assert.Equal(IPAddress.Loopback.ToString(), secondEndpoint.Host);
            var firstLifetime = first.Services.GetRequiredService<IHostApplicationLifetime>();
            var secondLifetime = second.Services.GetRequiredService<IHostApplicationLifetime>();
            var basicReady = await Task.WhenAll(
                WaitUntilReadyAsync(firstEndpoint, firstLifetime, ReadinessTimeout),
                WaitUntilReadyAsync(secondEndpoint, secondLifetime, ReadinessTimeout));

            await Task.WhenAll(
                WaitUntilFullHookChainReadyAsync(
                    firstEndpoint,
                    firstLifetime,
                    firstDiagnostics,
                    firstObserver,
                    firstDirectory,
                    basicReady[0],
                    ReadinessTimeout),
                WaitUntilFullHookChainReadyAsync(
                    secondEndpoint,
                    secondLifetime,
                    secondDiagnostics,
                    secondObserver,
                    secondDirectory,
                    basicReady[1],
                    ReadinessTimeout));
        }
        finally
        {
            await Task.WhenAll(
                StopApplicationIfRunningAsync(first),
                StopApplicationIfRunningAsync(second));
            await first.DisposeAsync();
            await second.DisposeAsync();
            DeleteDirectory(firstDirectory);
            DeleteDirectory(secondDirectory);
        }
    }

    [Fact]
    public async Task FullChainReadiness_StoppedHostReportsStateWithoutLaunchingHook()
    {
        var directory = CreateIsolatedDirectory("stopped-host");
        var desktopDiagnostics = new CollectingDesktopDiagnosticLogger();
        var requestObserver = new HookRequestObserver();
        var application = BuildDynamicDesktopHost(
            directory,
            desktopDiagnostics,
            requestObserver);
        try
        {
            await DesktopHost.InitializeAsync(application, CancellationToken.None);
            await StartApplicationAsync(application, HostStartupTimeout);
            var endpoint = GetBoundHookEndpoint(application);
            var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
            var basicReadyAt = await WaitUntilReadyAsync(endpoint, lifetime, ReadinessTimeout);
            await StopApplicationIfRunningAsync(application);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WaitUntilFullHookChainReadyAsync(
                    endpoint,
                    lifetime,
                    desktopDiagnostics,
                    requestObserver,
                    directory,
                    basicReadyAt,
                    TimeSpan.FromSeconds(2)));

            Assert.Contains("HostState=stopped", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await StopApplicationIfRunningAsync(application);
            await application.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    private static WebApplication BuildDynamicDesktopHost(
        string directory,
        CollectingDesktopDiagnosticLogger desktopDiagnostics,
        HookRequestObserver requestObserver)
    {
        var application = DesktopHost.Build(
            new DesktopRuntimeOptions
            {
                TestIsolationEnabled = true,
                EventsFilePath = Path.Combine(directory, "events.json"),
                LoopbackPort = 0,
            },
            diagnosticLogger: desktopDiagnostics);
        application.Use(requestObserver.ObserveAsync);
        return application;
    }

    private static WebApplication BuildGetOnlyHost(HookRequestObserver requestObserver)
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(
                IPAddress.Loopback,
                0,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
        });
        var application = builder.Build();
        application.Use(requestObserver.ObserveAsync);
        application.MapGet(
            DesktopHttpContract.EventsPath,
            () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        return application;
    }

    private static Uri GetBoundHookEndpoint(WebApplication application)
    {
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var listenerUri = new Uri(Assert.Single(addresses ?? []));
        Assert.Equal(IPAddress.Loopback.ToString(), listenerUri.Host);
        Assert.InRange(listenerUri.Port, 1, 65535);
        return new Uri(listenerUri, DesktopHttpContract.EventsPath);
    }

    private static async Task StopApplicationIfRunningAsync(WebApplication application)
    {
        var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
        if (!lifetime.ApplicationStarted.IsCancellationRequested
            || lifetime.ApplicationStopped.IsCancellationRequested)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(BusinessRequestTimeout);
        await application.StopAsync(timeout.Token);
    }

    private static string CreateIsolatedDirectory(string label)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Desktop-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateProbePayload(out string turnId)
    {
        turnId = $"readiness-{Guid.NewGuid():N}";
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "Stop",
            ["session_id"] = $"readiness-{Guid.NewGuid():N}",
            ["turn_id"] = turnId,
            ["cwd"] = "C:\\Test\\ReadinessProbe",
            ["last_assistant_message"] = "readiness probe",
        });
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

    private static async Task<DateTimeOffset> WaitUntilReadyAsync(
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
                    return DateTimeOffset.UtcNow;
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

    private static async Task<DateTimeOffset> WaitUntilFullHookChainReadyAsync(
        Uri endpoint,
        IHostApplicationLifetime lifetime,
        CollectingDesktopDiagnosticLogger desktopDiagnostics,
        HookRequestObserver requestObserver,
        string isolationRoot,
        DateTimeOffset basicReadyAt,
        TimeSpan timeout,
        TimeSpan? forwardTimeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;
        var lastDiagnostic = "none";
        forwardTimeout ??= TestHookForwardTimeout;

        while (stopwatch.Elapsed < timeout)
        {
            var hostState = GetHostState(lifetime);
            if (!string.Equals(hostState, "running", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    CreateFullChainReadinessDiagnostic(
                        endpoint,
                        stopwatch.Elapsed,
                        hostState,
                        basicReadyAt,
                        attempt,
                        lastDiagnostic));
            }

            attempt++;
            var payload = CreateProbePayload(out var turnId);
            var turnHash = CodexEventTransformer.HashIdentifier(turnId)!;
            var diagnosticPath = Path.Combine(
                isolationRoot,
                $"hook-readiness-{attempt}.ndjson");
            var checkpoint = requestObserver.CreateCheckpoint();
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var processTimeout = remaining < HookProcessTimeout ? remaining : HookProcessTimeout;
            HookProcessResult result;
            try
            {
                result = await RunHookProcessAsync(
                    payload,
                    diagnosticPath,
                    isolationRoot,
                    endpoint.Port,
                    forwardTimeout,
                    processTimeout,
                    TestHookConnectTimeout);
            }
            catch (TimeoutException)
            {
                lastDiagnostic = "hook_process_timeout";
                break;
            }

            var hookDiagnostic = ReadHookDiagnostic(diagnosticPath);
            var observation = requestObserver.GetObservationSince(checkpoint);
            var desktopDiagnostic = FindDesktopDiagnostic(desktopDiagnostics, turnHash);
            if (IsSuccessfulHookChain(
                    result,
                    hookDiagnostic,
                    endpoint,
                    observation,
                    desktopDiagnostic))
            {
                return DateTimeOffset.UtcNow;
            }

            lastDiagnostic = CreateHookChainDiagnostic(
                result,
                hookDiagnostic,
                endpoint,
                lifetime,
                basicReadyAt,
                observation,
                desktopDiagnostic,
                "full_chain_probe");
            if (result.TargetEndpoint != endpoint
                || string.Equals(
                    hookDiagnostic.Result,
                    HookErrorCodes.ForwardRejected,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    CreateFullChainReadinessDiagnostic(
                        endpoint,
                        stopwatch.Elapsed,
                        GetHostState(lifetime),
                        basicReadyAt,
                        attempt,
                        lastDiagnostic));
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
            CreateFullChainReadinessDiagnostic(
                endpoint,
                stopwatch.Elapsed,
                GetHostState(lifetime),
                basicReadyAt,
                attempt,
                lastDiagnostic));
    }

    private static async Task WaitForDesktopObservationAsync(
        HookRequestObserver requestObserver,
        HookRequestCheckpoint checkpoint,
        CollectingDesktopDiagnosticLogger desktopDiagnostics,
        string turnHash,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var observation = requestObserver.GetObservationSince(checkpoint);
            if (observation.Received && FindDesktopDiagnostic(desktopDiagnostics, turnHash) is not null)
            {
                return;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(
                remaining < ReadinessPollInterval ? remaining : ReadinessPollInterval);
        }
    }

    private static DesktopDiagnosticEvent? FindDesktopDiagnostic(
        CollectingDesktopDiagnosticLogger logger,
        string turnHash) =>
        logger.Events.LastOrDefault(item =>
            string.Equals(item.TurnIdHash, turnHash, StringComparison.Ordinal));

    private static HookDiagnosticSnapshot ReadHookDiagnostic(string path)
    {
        using var document = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        var root = document.RootElement;
        return new HookDiagnosticSnapshot(
            root.GetProperty("result").GetString() ?? "missing_result",
            root.TryGetProperty("httpStatus", out var status)
                && status.ValueKind == JsonValueKind.Number
                ? status.GetInt32()
                : null,
            root.GetProperty("elapsedMs").GetInt64(),
            root.TryGetProperty("failureStage", out var failureStage)
                ? failureStage.GetString()
                : null,
            root.TryGetProperty("exceptionType", out var exceptionType)
                ? exceptionType.GetString()
                : null);
    }

    private static bool IsSuccessfulHookChain(
        HookProcessResult result,
        HookDiagnosticSnapshot hookDiagnostic,
        Uri endpoint,
        HookRequestObservation observation,
        DesktopDiagnosticEvent? desktopDiagnostic) =>
        result.ExitCode == 0
        && string.Equals(
            result.StandardOutput,
            HookApplication.StopHookContinueResponse,
            StringComparison.Ordinal)
        && string.IsNullOrEmpty(result.StandardError)
        && result.TargetEndpoint == endpoint
        && string.Equals(hookDiagnostic.Result, ForwardResult.SuccessCode, StringComparison.Ordinal)
        && hookDiagnostic.HttpStatusCode == StatusCodes.Status202Accepted
        && observation.Received
        && observation.Completed
        && observation.LastStatusCode == StatusCodes.Status202Accepted
        && desktopDiagnostic?.HttpStatusCode == StatusCodes.Status202Accepted;

    private static void AssertHookSucceeded(
        HookProcessResult result,
        HookDiagnosticSnapshot hookDiagnostic,
        Uri endpoint,
        IHostApplicationLifetime lifetime,
        DateTimeOffset fullChainReadyAt,
        HookRequestObservation observation,
        DesktopDiagnosticEvent? desktopDiagnostic)
    {
        Assert.True(
            IsSuccessfulHookChain(
                result,
                hookDiagnostic,
                endpoint,
                observation,
                desktopDiagnostic),
            CreateHookChainDiagnostic(
                result,
                hookDiagnostic,
                endpoint,
                lifetime,
                fullChainReadyAt,
                observation,
                desktopDiagnostic,
                "formal_hook_assertion"));
    }

    private static string CreateHookChainDiagnostic(
        HookProcessResult result,
        HookDiagnosticSnapshot hookDiagnostic,
        Uri endpoint,
        IHostApplicationLifetime lifetime,
        DateTimeOffset readinessAt,
        HookRequestObservation observation,
        DesktopDiagnosticEvent? desktopDiagnostic,
        string stage) =>
        $"Hook chain failed. TargetHost={endpoint.Host}; TargetPort={endpoint.Port}; "
        + $"EndpointPath={endpoint.AbsolutePath}; Protocol={endpoint.Scheme}; "
        + $"ResolvedTargetMatches={result.TargetEndpoint == endpoint}; "
        + $"HostState={GetHostState(lifetime)}; LastReadinessAt={readinessAt:O}; "
        + $"Stage={stage}; ProcessElapsedMs={result.Elapsed.TotalMilliseconds:F0}; "
        + $"ForwardElapsedMs={hookDiagnostic.ElapsedMilliseconds}; "
        + $"ForwardResult={hookDiagnostic.Result}; "
        + $"DesktopReceived={observation.Received}; "
        + $"DesktopCompleted={observation.Completed}; "
        + $"ObservedResponseStatus={observation.LastStatusCode}; "
        + $"DesktopStatus={desktopDiagnostic?.HttpStatusCode.ToString() ?? "none"}.";

    private static string CreateFullChainReadinessDiagnostic(
        Uri endpoint,
        TimeSpan elapsed,
        string hostState,
        DateTimeOffset basicReadyAt,
        int attemptCount,
        string lastDiagnostic) =>
        $"Full Hook chain readiness failed. TargetHost={endpoint.Host}; "
        + $"TargetPort={endpoint.Port}; EndpointPath={endpoint.AbsolutePath}; "
        + $"Protocol={endpoint.Scheme}; WaitedMs={elapsed.TotalMilliseconds:F0}; "
        + $"HostState={hostState}; BasicReadinessAt={basicReadyAt:O}; "
        + $"AttemptCount={attemptCount}; LastSafeDiagnostic={lastDiagnostic}.";

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
        int? isolatedPort,
        TimeSpan? forwardTimeout = null,
        TimeSpan? processTimeout = null,
        TimeSpan? connectTimeout = null)
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(HookApplication).Assembly.Location);
        Assert.False(string.IsNullOrWhiteSpace(assemblyDirectory));
        var hookPath = Path.Combine(assemblyDirectory, "AgentBell.Hook.exe");
        Assert.True(File.Exists(hookPath), $"Hook executable not found at {hookPath}");

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
        startInfo.Environment.Remove(HookEndpointResolver.TestModeEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestLoopbackPortEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestForwardTimeoutEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestConnectTimeoutEnvironmentVariable);
        startInfo.Environment.Remove(HookEndpointResolver.TestProcessTimeoutEnvironmentVariable);
        startInfo.Environment[DiagnosticLoggerFactory.EnabledEnvironmentVariable] = "1";
        startInfo.Environment[DiagnosticLoggerFactory.PathEnvironmentVariable] = diagnosticPath;
        startInfo.Environment[HookEndpointResolver.TestModeEnvironmentVariable] = "1";
        startInfo.Environment[HookEndpointResolver.TestProcessTimeoutEnvironmentVariable] =
            checked((int)TestHookProcessHardTimeout.TotalMilliseconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (isolatedPort is not null)
        {
            startInfo.Environment[HookEndpointResolver.TestLoopbackPortEnvironmentVariable] =
                isolatedPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        var codexHome = Path.Combine(isolationRoot, "codex-home");
        var dataHome = Path.Combine(isolationRoot, "data-home");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(dataHome);
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment[DesktopRuntimeOptions.DataHomeEnvironmentVariable] = dataHome;
        var targetEndpoint = HookEndpointResolver.Resolve(name =>
            startInfo.Environment.TryGetValue(name, out var value) ? value : null);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Flush();
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(processTimeout ?? HookProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            stopwatch.Stop();
            throw new TimeoutException(
                $"Hook process timeout. TargetHost={targetEndpoint.Host}; "
                + $"TargetPort={targetEndpoint.Port}; EndpointPath={targetEndpoint.AbsolutePath}; "
                + $"Protocol={targetEndpoint.Scheme}; "
                + $"WaitedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");
        }

        stopwatch.Stop();
        return new HookProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask,
            targetEndpoint,
            stopwatch.Elapsed);
    }

    private sealed record HookDiagnosticSnapshot(
        string Result,
        int? HttpStatusCode,
        long ElapsedMilliseconds,
        string? FailureStage,
        string? ExceptionType);

    private sealed record HookProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        Uri TargetEndpoint,
        TimeSpan Elapsed);

    private sealed class HookRequestObserver
    {
        private long _received;
        private long _completed;
        private int _lastStatusCode;

        public HookRequestCheckpoint CreateCheckpoint() =>
            new(Interlocked.Read(ref _received), Interlocked.Read(ref _completed));

        public HookRequestObservation GetObservationSince(HookRequestCheckpoint checkpoint) =>
            new(
                Interlocked.Read(ref _received) > checkpoint.Received,
                Interlocked.Read(ref _completed) > checkpoint.Completed,
                Volatile.Read(ref _lastStatusCode));

        public async Task ObserveAsync(HttpContext context, RequestDelegate next)
        {
            var isHookRequest = HttpMethods.IsPost(context.Request.Method)
                && string.Equals(
                    context.Request.Path.Value,
                    DesktopHttpContract.EventsPath,
                    StringComparison.Ordinal);
            if (isHookRequest)
            {
                Interlocked.Increment(ref _received);
            }

            try
            {
                await next(context);
            }
            finally
            {
                if (isHookRequest)
                {
                    Volatile.Write(ref _lastStatusCode, context.Response.StatusCode);
                    Interlocked.Increment(ref _completed);
                }
            }
        }
    }

    private sealed record HookRequestCheckpoint(long Received, long Completed);

    private sealed record HookRequestObservation(
        bool Received,
        bool Completed,
        int LastStatusCode);
}
