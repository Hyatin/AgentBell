using System.Net;
using AgentBell.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentBell.Desktop;

/// <summary>Builds the M1 loopback-only ASP.NET Core host.</summary>
public static class DesktopHost
{
    /// <summary>The only address on which M1 accepts Hook events.</summary>
    public const string ListenAddress = "127.0.0.1";

    /// <summary>The fixed M1 loopback ingestion port.</summary>
    public const int ListenPort = 17863;

    /// <summary>Builds the production-shaped Desktop host without starting it.</summary>
    public static WebApplication Build(
        DesktopRuntimeOptions? runtimeOptions = null,
        IEventPublisher? eventPublisher = null,
        IDesktopDiagnosticLogger? diagnosticLogger = null)
    {
        runtimeOptions ??= DesktopRuntimeOptions.CreateDefault();
        runtimeOptions.Validate();

        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = DesktopHttpContract.MaxRequestBodyBytes;
            options.Listen(
                IPAddress.Parse(ListenAddress),
                runtimeOptions.LoopbackPort,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
        });

        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton<IEventStore>(services =>
            new JsonEventStore(services.GetRequiredService<DesktopRuntimeOptions>().EventsFilePath));
        builder.Services.AddSingleton<CodexEventTransformer>();
        builder.Services.AddSingleton(eventPublisher ?? NoOpEventPublisher.Instance);
        builder.Services.AddSingleton<EventPipeline>();
        builder.Services.AddSingleton(
            diagnosticLogger ?? DesktopDiagnosticLoggerFactory.CreateFromEnvironment());

        var application = builder.Build();
        application.MapPost(
            DesktopHttpContract.EventsPath,
            CodexEventIngestion.HandleAsync);

        return application;
    }

    /// <summary>Restores recent events and sequence state before accepting requests.</summary>
    public static Task InitializeAsync(
        WebApplication application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.Services
            .GetRequiredService<EventPipeline>()
            .InitializeAsync(cancellationToken);
    }
}

/// <summary>Contains local-only runtime paths for the Desktop process.</summary>
public sealed record DesktopRuntimeOptions
{
    /// <summary>Overrides the production data root for isolated automated tests.</summary>
    public const string DataHomeEnvironmentVariable = "AGENTBELL_DATA_HOME";

    /// <summary>Enables explicit test-only listener overrides when exactly 1.</summary>
    public const string TestModeEnvironmentVariable = "AGENTBELL_TEST_MODE";

    /// <summary>Supplies the isolated Hook listener port in test mode.</summary>
    public const string TestLoopbackPortEnvironmentVariable = "AGENTBELL_TEST_LOOPBACK_PORT";

    /// <summary>Supplies the isolated LAN/WebSocket listener port in test mode.</summary>
    public const string TestLanPortEnvironmentVariable = "AGENTBELL_TEST_LAN_PORT";

    /// <summary>Gets the isolated data root or the production AgentBell data directory.</summary>
    public string? DataDirectoryPath { get; init; }

    /// <summary>Gets the local sanitized event-history path.</summary>
    public required string EventsFilePath { get; init; }

    /// <summary>Gets the loopback port; production defaults to the stable port 17863.</summary>
    public int LoopbackPort { get; init; } = DesktopHost.ListenPort;

    /// <summary>Gets the first LAN port candidate.</summary>
    public int LanFirstPort { get; init; } = LanPortRange.FirstPort;

    /// <summary>Gets the last LAN port candidate.</summary>
    public int LanLastPort { get; init; } = LanPortRange.LastPort;

    /// <summary>Gets a loopback-only LAN override available only to isolated tests.</summary>
    public IPAddress? LanAddressOverride { get; init; }

    /// <summary>Gets whether non-production ports and loopback LAN are explicitly enabled.</summary>
    public bool TestIsolationEnabled { get; init; }

    /// <summary>Gets the DPAPI-protected M2 configuration path.</summary>
    public string? ConfigFilePath { get; init; }

    /// <summary>Gets the generated pairing QR PNG path.</summary>
    public string? PairingQrCodePath { get; init; }

    /// <summary>Gets the injectable rolling diagnostic log path.</summary>
    public string? DiagnosticLogPath { get; init; }

    /// <summary>Creates production defaults under the current user's local application data.</summary>
    public static DesktopRuntimeOptions CreateDefault(AgentBellPathResolver? pathResolver = null)
    {
        var testMode = string.Equals(
            Environment.GetEnvironmentVariable(TestModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        if (testMode)
        {
            var testDataRoot = Environment.GetEnvironmentVariable(DataHomeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(testDataRoot)
                || !TryReadTestPort(TestLoopbackPortEnvironmentVariable, out var testLoopbackPort)
                || !TryReadTestPort(TestLanPortEnvironmentVariable, out var testLanPort)
                || testLoopbackPort == testLanPort)
            {
                throw new InvalidOperationException("Isolated test runtime settings are incomplete.");
            }

            return CreateIsolatedTest(testDataRoot, testLoopbackPort, testLanPort);
        }

        var dataRoot = (pathResolver ?? new AgentBellPathResolver()).DataDirectory;

        return new DesktopRuntimeOptions
        {
            DataDirectoryPath = dataRoot,
            EventsFilePath = Path.Combine(dataRoot, "events.json"),
            ConfigFilePath = Path.Combine(dataRoot, "config.json"),
            PairingQrCodePath = Path.Combine(
                dataRoot,
                "pairing",
                "agentbell-pairing.png"),
            DiagnosticLogPath = Path.Combine(dataRoot, "logs", "tray.ndjson"),
        };
    }

    /// <summary>Creates a loopback-only runtime whose ports and files cannot overlap production.</summary>
    public static DesktopRuntimeOptions CreateIsolatedTest(
        string dataRoot,
        int loopbackPort,
        int lanPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ValidateTestPort(loopbackPort, nameof(loopbackPort));
        ValidateTestPort(lanPort, nameof(lanPort), allowDynamic: true);
        if (loopbackPort != 0 && loopbackPort == lanPort)
        {
            throw new ArgumentException("Isolated Hook and LAN ports must differ.");
        }

        var fullDataRoot = Path.GetFullPath(dataRoot);
        return new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            DataDirectoryPath = fullDataRoot,
            EventsFilePath = Path.Combine(fullDataRoot, "events.json"),
            ConfigFilePath = Path.Combine(fullDataRoot, "config.json"),
            PairingQrCodePath = Path.Combine(fullDataRoot, "pairing", "agentbell-pairing.png"),
            DiagnosticLogPath = Path.Combine(fullDataRoot, "logs", "tray.ndjson"),
            LoopbackPort = loopbackPort,
            LanFirstPort = lanPort,
            LanLastPort = lanPort,
            LanAddressOverride = IPAddress.Loopback,
        };
    }

    /// <summary>Rejects accidental non-production listener changes outside explicit test mode.</summary>
    public void Validate()
    {
        if (!TestIsolationEnabled)
        {
            if (LoopbackPort != DesktopHost.ListenPort
                || LanFirstPort != LanPortRange.FirstPort
                || LanLastPort != LanPortRange.LastPort
                || LanAddressOverride is not null)
            {
                throw new InvalidOperationException("Listener overrides require explicit test isolation.");
            }

            return;
        }

        ValidateTestPort(LoopbackPort, nameof(LoopbackPort), allowDynamic: true);
        ValidateTestPort(LanFirstPort, nameof(LanFirstPort), allowDynamic: true);
        ValidateTestPort(LanLastPort, nameof(LanLastPort), allowDynamic: true);
        var dynamicLanPort = LanFirstPort == 0 && LanLastPort == 0;
        if (LanFirstPort > LanLastPort
            || (LanFirstPort == 0 || LanLastPort == 0) && !dynamicLanPort
            || LoopbackPort != 0
                && !dynamicLanPort
                && LoopbackPort >= LanFirstPort
                && LoopbackPort <= LanLastPort
            || LanAddressOverride is not null && !IPAddress.Loopback.Equals(LanAddressOverride))
        {
            throw new InvalidOperationException("Isolated listener settings are invalid.");
        }
    }

    private static bool TryReadTestPort(string variableName, out int port)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return int.TryParse(value, out port) && port is >= 1024 and <= 65535;
    }

    private static void ValidateTestPort(
        int port,
        string parameterName,
        bool allowDynamic = false)
    {
        if (allowDynamic && port == 0)
        {
            return;
        }

        if (port is < 1024 or > 65535 || port == DesktopHost.ListenPort)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
