using System.Net;
using System.Net.Http.Headers;
using AgentBell.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentBell.Desktop;

/// <summary>Builds the logically isolated authenticated M2 LAN listener.</summary>
public static class LanHost
{
    /// <summary>The minimal unauthenticated liveness path.</summary>
    public const string HealthPath = "/health";

    /// <summary>The local pairing browser page path.</summary>
    public const string PairingPagePath = "/pair";

    /// <summary>The authenticated LAN status path.</summary>
    public const string StatusPath = "/api/v1/status";

    /// <summary>Builds a LAN host after validating a single RFC1918 address and M2 port.</summary>
    public static WebApplication Build(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        WebSocketConnectionManager connectionManager,
        IDesktopDiagnosticLogger diagnosticLogger) =>
        BuildCore(
            address,
            port,
            pairing,
            eventPipeline,
            connectionManager,
            diagnosticLogger,
            requirePrivateAddress: true);

    internal static WebApplication BuildForTesting(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        WebSocketConnectionManager connectionManager,
        IDesktopDiagnosticLogger diagnosticLogger) =>
        BuildCore(
            address,
            port,
            pairing,
            eventPipeline,
            connectionManager,
            diagnosticLogger,
            requirePrivateAddress: false);

    private static WebApplication BuildCore(
        IPAddress address,
        int port,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        WebSocketConnectionManager connectionManager,
        IDesktopDiagnosticLogger diagnosticLogger,
        bool requirePrivateAddress)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(eventPipeline);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(diagnosticLogger);
        if (requirePrivateAddress && !LanAddressResolver.IsPrivateIpv4(address))
        {
            throw new ArgumentException("LAN listener requires one RFC1918 IPv4 address.", nameof(address));
        }

        if (requirePrivateAddress && !LanPortRange.Contains(port)
            || !requirePrivateAddress && port != 0 && port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(
                address,
                port,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
        });

        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        application.MapGet(HealthPath, async context =>
        {
            SetNoStore(context.Response);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                "{\"status\":\"ok\"}",
                context.RequestAborted).ConfigureAwait(false);
        });

        application.MapGet(PairingPagePath, async context =>
        {
            SetNoStore(context.Response);
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
                + "connect-src 'self' ws:; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                PairingPageProvider.ReadHtml(),
                context.RequestAborted).ConfigureAwait(false);
        });

        application.MapGet(StatusPath, async context =>
        {
            var authentication = LanRequestAuthenticator.AuthenticateBearer(
                context.Request,
                pairing.Token);
            RecordAuthentication(diagnosticLogger, authentication, StatusPath);
            if (authentication != LanAuthenticationResult.Authenticated)
            {
                context.Response.StatusCode = authentication == LanAuthenticationResult.Missing
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden;
                SetNoStore(context.Response);
                return;
            }

            var history = await eventPipeline.GetHistoryAsync(
                long.MaxValue,
                context.RequestAborted).ConfigureAwait(false);
            SetNoStore(context.Response);
            await context.Response.WriteAsJsonAsync(
                new StatusResponse
                {
                    DeviceName = pairing.Configuration.DeviceName ?? "Windows PC",
                    DeviceId = pairing.Configuration.DeviceId ?? string.Empty,
                    LanAddress = address.ToString(),
                    LanPort = context.Connection.LocalPort,
                    LatestSequence = history.LatestSequence,
                    EventCount = history.EventCount,
                },
                context.RequestAborted).ConfigureAwait(false);
        });

        application.MapGet(AgentBellProtocol.WebSocketPath, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var authentication = LanRequestAuthenticator.AuthenticateWebSocket(
                context.Request,
                pairing.Token);
            RecordAuthentication(diagnosticLogger, authentication, "websocket_upgrade");
            if (authentication != LanAuthenticationResult.Authenticated)
            {
                context.Response.StatusCode = authentication == LanAuthenticationResult.Missing
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await connectionManager.RunClientAsync(
                socket,
                pairing,
                eventPipeline,
                context.RequestAborted).ConfigureAwait(false);
        });

        return application;
    }

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static void RecordAuthentication(
        IDesktopDiagnosticLogger logger,
        LanAuthenticationResult result,
        string eventType)
    {
        try
        {
            logger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = eventType,
                HttpStatusCode = result switch
                {
                    LanAuthenticationResult.Authenticated => 200,
                    LanAuthenticationResult.Missing => 401,
                    _ => 403,
                },
                ElapsedMilliseconds = 0,
                PersistenceSucceeded = true,
                Authenticated = result == LanAuthenticationResult.Authenticated,
                Result = result.ToString().ToLowerInvariant(),
            });
        }
        catch
        {
            // Authentication diagnostics never affect a request.
        }
    }
}

internal enum LanAuthenticationResult
{
    Missing,
    Invalid,
    Authenticated,
}

internal static class LanRequestAuthenticator
{
    public static LanAuthenticationResult AuthenticateBearer(
        HttpRequest request,
        PairingToken token)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values)
            || values.Count != 1)
        {
            return LanAuthenticationResult.Missing;
        }

        var value = values[0];
        if (string.IsNullOrWhiteSpace(value)
            || !AuthenticationHeaderValue.TryParse(value, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return LanAuthenticationResult.Missing;
        }

        return token.Matches(header.Parameter)
            ? LanAuthenticationResult.Authenticated
            : LanAuthenticationResult.Invalid;
    }

    public static LanAuthenticationResult AuthenticateWebSocket(
        HttpRequest request,
        PairingToken token)
    {
        if (request.Headers.ContainsKey("Authorization"))
        {
            return AuthenticateBearer(request, token);
        }

        if (!request.Query.TryGetValue("access_token", out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
        {
            return LanAuthenticationResult.Missing;
        }

        return token.Matches(values[0])
            ? LanAuthenticationResult.Authenticated
            : LanAuthenticationResult.Invalid;
    }
}

/// <summary>Owns a successfully started M2 LAN application.</summary>
public sealed class LanServerInstance : IAsyncDisposable
{
    internal LanServerInstance(
        WebApplication application,
        IPAddress address,
        int port,
        string pairingUrl,
        bool qrCodeWritten)
    {
        Application = application;
        Address = address;
        Port = port;
        PairingUrl = pairingUrl;
        QrCodeWritten = qrCodeWritten;
    }

    /// <summary>Gets the started LAN application.</summary>
    public WebApplication Application { get; }

    /// <summary>Gets the exact bound private address.</summary>
    public IPAddress Address { get; }

    /// <summary>Gets the selected port.</summary>
    public int Port { get; }

    /// <summary>Gets the explicit fragment-based pairing output.</summary>
    public string PairingUrl { get; }

    /// <summary>Gets whether the pairing PNG was updated.</summary>
    public bool QrCodeWritten { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Application.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // A LAN shutdown failure cannot alter the M1 process result.
        }

        await Application.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Tries the fixed LAN port range without changing the loopback listener.</summary>
public sealed class LanServerStarter
{
    private readonly PairingQrCodeWriter _qrCodeWriter;

    /// <summary>Initializes a starter with the QR writer.</summary>
    public LanServerStarter(PairingQrCodeWriter qrCodeWriter)
    {
        _qrCodeWriter = qrCodeWriter ?? throw new ArgumentNullException(nameof(qrCodeWriter));
    }

    /// <summary>Starts the first available port and persists that exact choice.</summary>
    public async Task<LanServerStartResult> TryStartAsync(
        IPAddress address,
        string qrCodePath,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        WebSocketConnectionManager connectionManager,
        IDesktopDiagnosticLogger diagnosticLogger,
        CancellationToken cancellationToken)
        => await TryStartAsync(
            address,
            qrCodePath,
            pairing,
            eventPipeline,
            connectionManager,
            diagnosticLogger,
            LanPortRange.FirstPort,
            LanPortRange.LastPort,
            testIsolationEnabled: false,
            cancellationToken).ConfigureAwait(false);

    internal async Task<LanServerStartResult> TryStartAsync(
        IPAddress address,
        string qrCodePath,
        PairingConfigurationSession pairing,
        EventPipeline eventPipeline,
        WebSocketConnectionManager connectionManager,
        IDesktopDiagnosticLogger diagnosticLogger,
        int firstPort,
        int lastPort,
        bool testIsolationEnabled,
        CancellationToken cancellationToken)
    {
        for (var port = firstPort; port <= lastPort; port++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WebApplication? application = null;
            try
            {
                application = testIsolationEnabled
                    ? LanHost.BuildForTesting(
                        address,
                        port,
                        pairing,
                        eventPipeline,
                        connectionManager,
                        diagnosticLogger)
                    : LanHost.Build(
                        address,
                        port,
                        pairing,
                        eventPipeline,
                        connectionManager,
                        diagnosticLogger);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                var selectedPort = ResolveSelectedPort(application, address, port);
                if (!await pairing.UpdateLanPortAsync(
                        selectedPort,
                        cancellationToken).ConfigureAwait(false))
                {
                    await application.StopAsync(cancellationToken).ConfigureAwait(false);
                    await application.DisposeAsync().ConfigureAwait(false);
                    return LanServerStartResult.Unavailable("config_port_write_failed");
                }

                var pairingUrl = testIsolationEnabled
                    ? PairingUrlBuilder.BuildForTesting(address, selectedPort, pairing)
                    : PairingUrlBuilder.Build(address, selectedPort, pairing);
                var qrWritten = await _qrCodeWriter.WriteAsync(
                    pairingUrl,
                    qrCodePath,
                    cancellationToken).ConfigureAwait(false);
                return LanServerStartResult.Available(new LanServerInstance(
                    application,
                    address,
                    selectedPort,
                    pairingUrl,
                    qrWritten));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (application is not null)
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch
            {
                if (application is not null)
                {
                    try
                    {
                        await application.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Continue to the next bounded port candidate.
                    }
                }
            }
        }

        return LanServerStartResult.Unavailable("lan_ports_unavailable");
    }

    private static int ResolveSelectedPort(
        WebApplication application,
        IPAddress expectedAddress,
        int requestedPort)
    {
        if (requestedPort != 0)
        {
            return requestedPort;
        }

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        if (addresses is null || addresses.Count != 1)
        {
            throw new InvalidOperationException("The isolated LAN listener address is unavailable.");
        }

        var listener = new Uri(addresses.Single());
        if (!string.Equals(listener.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(listener.Host, expectedAddress.ToString(), StringComparison.Ordinal)
            || listener.Port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("The isolated LAN listener address is invalid.");
        }

        return listener.Port;
    }
}

/// <summary>Describes LAN availability without exception or credential data.</summary>
public sealed record LanServerStartResult(
    bool IsAvailable,
    LanServerInstance? Server,
    string ResultCode)
{
    internal static LanServerStartResult Available(LanServerInstance server) =>
        new(true, server, "success");

    internal static LanServerStartResult Unavailable(string resultCode) =>
        new(false, null, resultCode);
}
