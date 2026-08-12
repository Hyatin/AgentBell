using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentBell.Localization;

namespace AgentBell.Desktop;

/// <summary>
/// Owns the shared loopback, LAN, pairing, storage, and WebSocket lifecycle used by
/// both the headless Desktop executable and the M4 Tray executable.
/// </summary>
public sealed class AgentBellRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly DesktopRuntimeOptions _runtimeOptions;
    private readonly IDesktopDiagnosticLogger _diagnosticLogger;
    private readonly LanAddressResolver _addressResolver;
    private readonly IPairingTokenProtector _tokenProtector;
    private readonly DesktopNotificationSettingsState _notificationSettings = new();

    private WebApplication? _loopbackApplication;
    private WebSocketConnectionManager? _connectionManager;
    private PairingConfigurationSession? _pairing;
    private LanServerInstance? _lanServer;
    private RuntimeServiceStatus _localStatus = RuntimeServiceStatus.Stopped;
    private RuntimeServiceStatus _lanStatus = RuntimeServiceStatus.Stopped;
    private string _localResultCode = "stopped";
    private string _lanResultCode = "stopped";
    private bool _disposed;

    /// <summary>Initializes the shared runtime with production defaults or test collaborators.</summary>
    public AgentBellRuntime(
        DesktopRuntimeOptions? runtimeOptions = null,
        IDesktopDiagnosticLogger? diagnosticLogger = null,
        LanAddressResolver? addressResolver = null,
        IPairingTokenProtector? tokenProtector = null)
    {
        _runtimeOptions = runtimeOptions ?? DesktopRuntimeOptions.CreateDefault();
        _runtimeOptions.Validate();
        _diagnosticLogger = diagnosticLogger ?? NullDesktopDiagnosticLogger.Instance;
        _addressResolver = addressResolver ?? new LanAddressResolver();
        _tokenProtector = tokenProtector ?? new WindowsDpapiPairingTokenProtector();
    }

    /// <summary>Gets the local data paths used by this runtime.</summary>
    public DesktopRuntimeOptions RuntimeOptions => _runtimeOptions;

    /// <summary>Starts loopback first, then attempts LAN without making LAN failure fatal.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loopbackApplication is not null)
            {
                return;
            }

            _connectionManager = new WebSocketConnectionManager(_diagnosticLogger);
            var eventPublisher = new WebSocketEventPublisher(_connectionManager);
            var application = DesktopHost.Build(
                _runtimeOptions,
                eventPublisher,
                _diagnosticLogger,
                _notificationSettings);
            try
            {
                await DesktopHost.InitializeAsync(application, cancellationToken).ConfigureAwait(false);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _loopbackApplication = application;
                _localStatus = RuntimeServiceStatus.Running;
                _localResultCode = "running";
            }
            catch
            {
                _localStatus = RuntimeServiceStatus.Error;
                _localResultCode = "loopback_start_failed";
                await application.DisposeAsync().ConfigureAwait(false);
                _connectionManager = null;
                RecordStatus("runtime", _localResultCode);
                throw new AgentBellRuntimeException(_localResultCode);
            }

            await TryStartLanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_loopbackApplication is not null && _localStatus != RuntimeServiceStatus.Running)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Stops accepting Hook events and then closes LAN/WebSocket resources.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Waits for the shared loopback host shutdown signal.</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        var application = _loopbackApplication
            ?? throw new InvalidOperationException("The AgentBell runtime is not running.");
        return application.WaitForShutdownAsync(cancellationToken);
    }

    /// <summary>Returns a content-free runtime snapshot for UI and diagnostics.</summary>
    public async Task<AgentBellRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var application = _loopbackApplication;
        EventHistorySnapshot? history = null;
        if (application is not null)
        {
            try
            {
                history = await application.Services
                    .GetRequiredService<EventPipeline>()
                    .GetHistoryAsync(0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The UI can still display listener state while history is unavailable.
            }
        }

        var latest = history?.Events.LastOrDefault();
        return new AgentBellRuntimeSnapshot
        {
            LocalHookService = _localStatus,
            LanService = _lanStatus,
            LocalResultCode = _localResultCode,
            LanResultCode = _lanResultCode,
            LanAddress = _lanServer?.Address.ToString(),
            LanPort = _lanServer?.Port,
            WebSocketClientCount = _connectionManager?.ActiveConnectionCount ?? 0,
            LatestSequence = history?.LatestSequence ?? 0,
            EventCount = history?.EventCount ?? 0,
            LastEventTime = latest?.OccurredAt,
            RecentEvents = history?.Events ?? [],
            NotificationSettings = _notificationSettings.Current,
            PairingQrCodePath = _runtimeOptions.PairingQrCodePath,
            PairingQrAvailable = _lanServer?.QrCodeWritten == true
                && !string.IsNullOrWhiteSpace(_runtimeOptions.PairingQrCodePath)
                && File.Exists(_runtimeOptions.PairingQrCodePath),
        };
    }

    /// <summary>
    /// Returns the credential-bearing pairing URL only for an explicit pairing or
    /// confirmed clipboard action. Callers must never log the returned value.
    /// </summary>
    public string? GetPairingUrl() => _lanServer?.PairingUrl;

    /// <summary>Recreates the QR image with the existing token, preserving phone pairing.</summary>
    public async Task<bool> RegeneratePairingQrAsync(CancellationToken cancellationToken)
    {
        var pairingUrl = _lanServer?.PairingUrl;
        var destination = _runtimeOptions.PairingQrCodePath;
        if (string.IsNullOrWhiteSpace(pairingUrl) || string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        return await new PairingQrCodeWriter()
            .WriteAsync(pairingUrl, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Persists the Windows UI language without replacing pairing credentials.</summary>
    public async Task<bool> UpdateLanguageAsync(
        AppLanguage language,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pairing is not null)
            {
                return await _pairing.UpdateLanguageAsync(language, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(_runtimeOptions.ConfigFilePath))
            {
                return false;
            }

            var store = new AgentBellConfigStore(_runtimeOptions.ConfigFilePath);
            var load = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.PersistenceSucceeded || load.Configuration is null)
            {
                return false;
            }

            return await store.SaveAsync(
                load.Configuration with
                {
                    Language = AppLanguageValues.ToPersistedValue(language),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Persists and atomically applies local Windows notification settings.</summary>
    public async Task<bool> UpdateNotificationSettingsAsync(
        DesktopNotificationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool saved;
            if (_pairing is not null)
            {
                saved = await _pairing.UpdateNotificationSettingsAsync(settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(_runtimeOptions.ConfigFilePath))
            {
                var store = new AgentBellConfigStore(_runtimeOptions.ConfigFilePath);
                var load = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                saved = load.PersistenceSucceeded
                    && load.Configuration is not null
                    && await store.SaveAsync(
                        load.Configuration with
                        {
                            NotifyTaskCompletion = settings.NotifyTaskCompletion,
                            NotifyActionRequired = settings.NotifyActionRequired,
                            PermissionNotificationPolicy =
                                PermissionNotificationPolicyValues.ToPersistedValue(
                                    settings.PermissionNotificationPolicy),
                            LegacyNotifyPermissionRequests = null,
                            NotifyReplyAndConfirmationRequests =
                                settings.NotifyReplyAndConfirmationRequests,
                            DetectQuestionsInCompletedResponses =
                                settings.DetectQuestionsInCompletedResponses,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        },
                        cancellationToken).ConfigureAwait(false);
            }
            else
            {
                saved = false;
            }

            if (saved)
            {
                _notificationSettings.Update(settings);
            }

            return saved;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Gets the current token only for in-process sensitive-data scanning.</summary>
    internal string? GetSensitivePairingTokenForScan() => _pairing?.Token.Value;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
    }

    private async Task TryStartLanAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_runtimeOptions.ConfigFilePath)
                || string.IsNullOrWhiteSpace(_runtimeOptions.PairingQrCodePath))
            {
                SetLanUnavailable("lan_paths_unavailable");
                return;
            }

            var configuration = await new PairingConfigurationManager(
                    new AgentBellConfigStore(_runtimeOptions.ConfigFilePath),
                    _tokenProtector,
                    lanPortValidator: IsAllowedLanPort)
                .LoadOrCreateAsync(cancellationToken)
                .ConfigureAwait(false);
            _pairing = configuration.Session;
            if (!configuration.IsAvailable || _pairing is null)
            {
                SetLanUnavailable(configuration.ResultCode);
                return;
            }

            _notificationSettings.Update(_pairing.GetNotificationSettings());

            var address = _runtimeOptions.LanAddressOverride
                ?? _addressResolver.ResolveCurrent();
            if (address is null)
            {
                SetLanUnavailable("private_ipv4_unavailable");
                return;
            }

            var eventPipeline = _loopbackApplication!.Services.GetRequiredService<EventPipeline>();
            var startResult = await new LanServerStarter(new PairingQrCodeWriter())
                .TryStartAsync(
                    address,
                    _runtimeOptions.PairingQrCodePath,
                    _pairing,
                    eventPipeline,
                    _connectionManager!,
                    _diagnosticLogger,
                    _runtimeOptions.LanFirstPort,
                    _runtimeOptions.LanLastPort,
                    _runtimeOptions.TestIsolationEnabled,
                    cancellationToken)
                .ConfigureAwait(false);
            _lanServer = startResult.Server;
            if (!startResult.IsAvailable || _lanServer is null)
            {
                SetLanUnavailable(startResult.ResultCode);
                return;
            }

            _lanStatus = RuntimeServiceStatus.Available;
            _lanResultCode = "available";
            RecordStatus("lan-status", "available");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (_lanServer is not null)
            {
                await _lanServer.DisposeAsync().ConfigureAwait(false);
                _lanServer = null;
            }

            _pairing?.Dispose();
            _pairing = null;
            SetLanUnavailable("lan_start_failed");
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var loopback = _loopbackApplication;
        _loopbackApplication = null;
        if (loopback is not null)
        {
            try
            {
                await loopback.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown remains best-effort after the listener stops accepting events.
            }
        }

        if (_connectionManager is not null)
        {
            await _connectionManager.CloseAllAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_lanServer is not null)
        {
            await _lanServer.DisposeAsync().ConfigureAwait(false);
            _lanServer = null;
        }

        _pairing?.Dispose();
        _pairing = null;
        if (loopback is not null)
        {
            await loopback.DisposeAsync().ConfigureAwait(false);
        }

        _connectionManager = null;
        _localStatus = RuntimeServiceStatus.Stopped;
        _lanStatus = RuntimeServiceStatus.Stopped;
        _localResultCode = "stopped";
        _lanResultCode = "stopped";
    }

    private void SetLanUnavailable(string resultCode)
    {
        _lanStatus = RuntimeServiceStatus.Unavailable;
        _lanResultCode = resultCode;
        RecordStatus("lan-status", resultCode);
    }

    private bool IsAllowedLanPort(int port)
    {
        if (_runtimeOptions.TestIsolationEnabled
            && _runtimeOptions.LanFirstPort == 0
            && _runtimeOptions.LanLastPort == 0)
        {
            return port is >= 1024 and <= 65535;
        }

        return port >= _runtimeOptions.LanFirstPort
            && port <= _runtimeOptions.LanLastPort;
    }

    private void RecordStatus(string eventType, string resultCode)
    {
        try
        {
            _diagnosticLogger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = eventType,
                HttpStatusCode = 0,
                ElapsedMilliseconds = 0,
                PersistenceSucceeded = true,
                Result = resultCode,
            });
        }
        catch
        {
            // Diagnostics can never affect service lifecycle.
        }
    }
}

/// <summary>Reports a stable runtime startup failure without exposing exception text.</summary>
public sealed class AgentBellRuntimeException(string errorCode) : Exception(errorCode)
{
    /// <summary>Gets the stable content-free runtime error code.</summary>
    public string ErrorCode { get; } = errorCode;
}
