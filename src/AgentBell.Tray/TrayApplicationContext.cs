using System.Diagnostics;
using System.Globalization;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Integration;
using AgentBell.Localization;
using Microsoft.Win32;

namespace AgentBell.Tray;

/// <summary>Owns the localized NotifyIcon, shared runtime, integration actions, and shutdown.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly AgentBellRuntime _runtime;
    private readonly IntegrationService _integration;
    private readonly StartupRegistration _startup;
    private readonly string _androidApkPath;
    private readonly string _dataDirectory;
    private readonly IDesktopDiagnosticLogger _logger;
    private readonly object _exitGate = new();
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem _serviceStatusItem = null!;
    private ToolStripMenuItem _clientCountItem = null!;
    private ToolStripMenuItem _recentEventItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private MainForm? _mainForm;
    private Task _startupTask;
    private long? _lastObservedSequence;
    private bool _exiting;

    /// <summary>Initializes the per-user Tray context after culture has been selected.</summary>
    public TrayApplicationContext(
        AppLanguageService language,
        DesktopRuntimeOptions runtimeOptions,
        AgentBellPathResolver pathResolver,
        bool usedInvalidLanguageFallback = false)
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentNullException.ThrowIfNull(pathResolver);
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _dataDirectory = Path.GetDirectoryName(runtimeOptions.EventsFilePath)
            ?? throw new InvalidOperationException("AgentBell data path is unavailable.");
        _androidApkPath = pathResolver.AndroidApkPath;
        _logger = new RollingDesktopDiagnosticLogger(
            runtimeOptions.DiagnosticLogPath
                ?? Path.Combine(_dataDirectory, "logs", "tray.ndjson"));
        if (usedInvalidLanguageFallback)
        {
            RecordUiResult("invalid_language_fallback");
        }
        _runtime = new AgentBellRuntime(runtimeOptions, _logger);
        _integration = new IntegrationService(
            pathResolver.GetInstalledExecutablePath("AgentBell.Hook.exe"));
        _startup = new StartupRegistration(
            pathResolver.GetInstalledExecutablePath("AgentBell.Tray.exe"));

        _notifyIcon = new NotifyIcon
        {
            Text = "AgentBell",
            Icon = SystemIcons.Information,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        RebuildMenu();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RunUiTask(RefreshMenuAsync);
        _refreshTimer.Start();
        SystemEvents.SessionEnding += OnSessionEnding;
        _startupTask = StartRuntimeAsync();
    }

    /// <summary>Gets the shared runtime for the main window.</summary>
    public AgentBellRuntime Runtime => _runtime;

    /// <summary>Gets the Android APK path for display and installation help.</summary>
    public string AndroidApkPath => _androidApkPath;

    /// <summary>Gets current-user startup registration.</summary>
    public StartupRegistration Startup => _startup;

    /// <summary>Gets the Windows language service.</summary>
    public AppLanguageService Language { get; }

    /// <summary>Gets strings for the effective Windows UI language.</summary>
    public IAppLocalizer Localizer => Language.Localizer;

    /// <summary>Returns current integration status.</summary>
    public Task<CodexIntegrationResult> GetIntegrationStatusAsync(CancellationToken cancellationToken) =>
        _integration.ExecuteAsync("status", cancellationToken);

    /// <summary>Persists and immediately applies a supported Windows UI language.</summary>
    public async Task SetLanguageAsync(AppLanguage language)
    {
        await _startupTask.ConfigureAwait(true);
        if (!await _runtime.UpdateLanguageAsync(language, CancellationToken.None).ConfigureAwait(true))
        {
            MessageBox.Show(
                Localizer.Get("Settings_SaveLanguageFailed"),
                "AgentBell",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Language.SetLanguage(language);
        RebuildMenu();
        _mainForm?.ApplyLanguage();
        await RefreshMenuAsync().ConfigureAwait(true);
    }

    /// <summary>Posts a bounded single-instance IPC command onto the UI thread.</summary>
    public void PostIpcMessage(string message)
    {
        _uiContext.Post(
            _ =>
            {
                if (message == "show")
                {
                    ShowMainWindow();
                }
                else if (message == "shutdown")
                {
                    BeginExit();
                }
            },
            null);
    }

    /// <summary>Starts the shared receive services after a user stop.</summary>
    public async Task StartServicesAsync()
    {
        try
        {
            await _runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (AgentBellRuntimeException exception)
        {
            RecordUiResult(exception.ErrorCode);
            MessageBox.Show(
                Localizer.Get("Error_LocalServiceStartFailed"),
                "AgentBell",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>Stops the shared receive services without deleting any data.</summary>
    public async Task StopServicesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _runtime.StopAsync(timeout.Token).ConfigureAwait(true);
    }

    /// <summary>Installs or repairs the stable user-level Codex Hook.</summary>
    public async Task RepairIntegrationAsync()
    {
        var result = await _integration.ExecuteAsync("repair", CancellationToken.None)
            .ConfigureAwait(true);
        MessageBox.Show(
            result.Success
                ? Localizer.Get("Codex_IntegrationInstalled")
                : UserFacingIntegrationError(result),
            Localizer.Get("Codex_IntegrationTitle"),
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>Opens the Android APK directory without requiring adb or Android Studio.</summary>
    public void OpenAndroidFolder()
    {
        var directory = Path.GetDirectoryName(_androidApkPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenFolder(directory);
        }
    }

    /// <summary>Exports the deliberately minimal, automatically scanned diagnostic ZIP.</summary>
    public async Task ExportDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Localizer.Get("Diagnostics_Filter"),
            FileName = $"AgentBell-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = "zip",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var integration = await GetIntegrationStatusAsync(CancellationToken.None)
            .ConfigureAwait(true);
        var result = await new DiagnosticExporter().ExportAsync(
            dialog.FileName,
            _runtime,
            integration.State.ToString(),
            integration.AgentBellHookCount,
            integration.HooksPath,
            CancellationToken.None).ConfigureAwait(true);
        MessageBox.Show(
            result.Success
                ? Localizer.Get("Diagnostics_ExportSucceeded")
                : Localizer.Format("Diagnostics_ExportFailed", result.Code),
            "AgentBell",
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    /// <summary>Begins the ordinary graceful Tray shutdown path.</summary>
    public void BeginExit()
    {
        lock (_exitGate)
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
        }

        RunUiTask(ExitAsync);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            _refreshTimer.Dispose();
            _menu?.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RebuildMenu()
    {
        var replacement = new ContextMenuStrip();
        replacement.Items.Add(Localizer.Get("Tray_OpenAgentBell"), null, (_, _) => ShowMainWindow());
        replacement.Items.Add(Localizer.Get("Tray_ShowQrCode"), null, (_, _) => ShowMainWindow());
        replacement.Items.Add(new ToolStripSeparator());
        _serviceStatusItem = new ToolStripMenuItem(
            Localizer.Format(
                "Status_ConnectionServices",
                Localizer.Get("Status_Starting"),
                Localizer.Get("Status_Starting")))
        {
            Enabled = false,
        };
        _clientCountItem = new ToolStripMenuItem(Localizer.Format("Status_PhoneConnectionCount", 0))
        {
            Enabled = false,
        };
        _recentEventItem = new ToolStripMenuItem(Localizer.Get("Status_NoRecentEvent"))
        {
            Enabled = false,
        };
        replacement.Items.Add(_serviceStatusItem);
        replacement.Items.Add(_clientCountItem);
        replacement.Items.Add(_recentEventItem);
        replacement.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem(Localizer.Get("Tray_StartWithWindows"));
        _startupItem.Click += (_, _) => RunUiTask(ToggleStartupAsync);
        replacement.Items.Add(_startupItem);
        replacement.Items.Add(
            Localizer.Get("Action_RepairCodexIntegration"),
            null,
            (_, _) => RunUiTask(RepairIntegrationAsync));
        replacement.Items.Add(
            Localizer.Get("Action_CheckCodexIntegration"),
            null,
            (_, _) => RunUiTask(CheckIntegrationAsync));
        replacement.Items.Add(new ToolStripSeparator());
        replacement.Items.Add(
            Localizer.Get("Action_OpenAndroidFolder"),
            null,
            (_, _) => OpenAndroidFolder());
        replacement.Items.Add(
            Localizer.Get("Action_OpenDataFolder"),
            null,
            (_, _) => OpenFolder(_dataDirectory));
        replacement.Items.Add(
            Localizer.Get("Action_ExportDiagnostics"),
            null,
            (_, _) => RunUiTask(ExportDiagnosticsAsync));
        replacement.Items.Add(Localizer.Get("Common_About"), null, (_, _) => ShowAbout());
        replacement.Items.Add(new ToolStripSeparator());
        replacement.Items.Add(Localizer.Get("Common_Exit"), null, (_, _) => BeginExit());

        var previous = _menu;
        _menu = replacement;
        _notifyIcon.ContextMenuStrip = replacement;
        previous?.Dispose();
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            await _runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (AgentBellRuntimeException exception)
        {
            RecordUiResult(exception.ErrorCode);
        }
        catch
        {
            RecordUiResult("runtime_start_failed");
        }

        await RefreshMenuAsync().ConfigureAwait(true);
    }

    private void ShowMainWindow()
    {
        _mainForm ??= new MainForm(this);
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
        _mainForm.BeginRefresh();
    }

    private async Task RefreshMenuAsync()
    {
        var snapshot = await _runtime.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
        var localStatus = LocalizeRuntimeStatus(snapshot.LocalHookService);
        var lanStatus = LocalizeRuntimeStatus(snapshot.LanService);
        _serviceStatusItem.Text = Localizer.Format("Status_ConnectionServices", localStatus, lanStatus);
        _clientCountItem.Text = Localizer.Format(
            "Status_PhoneConnectionCount",
            snapshot.WebSocketClientCount.ToString(CultureInfo.InvariantCulture));
        _recentEventItem.Text = snapshot.LastEventTime is null
            ? Localizer.Get("Status_NoRecentEvent")
            : Localizer.Format(
                "Status_RecentEvent",
                snapshot.LatestSequence.ToString(CultureInfo.InvariantCulture),
                snapshot.LastEventTime.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        _startupItem.Checked = _startup.Status().State == StartupRegistrationState.Enabled;
        _notifyIcon.Text = Localizer.Format("Tray_Tooltip", localStatus);
        ShowCompletionNotificationIfNew(snapshot.LatestSequence);
    }

    private void ShowCompletionNotificationIfNew(long latestSequence)
    {
        if (_lastObservedSequence is null || latestSequence < _lastObservedSequence.Value)
        {
            _lastObservedSequence = latestSequence;
            return;
        }

        if (latestSequence <= _lastObservedSequence.Value)
        {
            return;
        }

        _lastObservedSequence = latestSequence;
        var notification = WindowsNotificationProjection.Create(Localizer);
        _notifyIcon.ShowBalloonTip(
            timeout: 5000,
            notification.Title,
            notification.Body,
            ToolTipIcon.Info);
    }

    private Task ToggleStartupAsync()
    {
        var current = _startup.Status();
        var result = current.State == StartupRegistrationState.Enabled
            ? _startup.Disable()
            : _startup.Enable();
        if (!result.Success)
        {
            MessageBox.Show(
                Localizer.Get("Error_StartupUpdateFailed"),
                "AgentBell",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        return RefreshMenuAsync();
    }

    private async Task CheckIntegrationAsync()
    {
        var result = await GetIntegrationStatusAsync(CancellationToken.None).ConfigureAwait(true);
        MessageBox.Show(
            result.Success
                ? Localizer.Format("Codex_IntegrationStatus", LocalizeIntegrationStatus(result.State))
                : UserFacingIntegrationError(result),
            Localizer.Get("Codex_IntegrationTitle"),
            MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ExitAsync()
    {
        _refreshTimer.Stop();
        try
        {
            await _startupTask.ConfigureAwait(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _runtime.StopAsync(timeout.Token).ConfigureAwait(true);
            await _runtime.DisposeAsync().ConfigureAwait(true);
        }
        catch
        {
            RecordUiResult("shutdown_failed");
        }
        finally
        {
            _notifyIcon.Visible = false;
            ExitThread();
        }
    }

    private string UserFacingIntegrationError(CodexIntegrationResult result) => result.Code switch
    {
        "hooks_json_invalid" or "hooks_root_invalid" or "hooks_structure_invalid" =>
            Localizer.Format("Codex_HooksInvalid", result.HooksPath ?? Localizer.Get("Common_NotSet")),
        "manual_review_required" =>
            Localizer.Format(
                "Codex_ManualReviewRequired",
                result.HooksPath ?? Localizer.Get("Common_NotSet")),
        "hook_executable_missing" => Localizer.Get("Codex_HookExecutableMissing"),
        "codex_home_invalid" => Localizer.Get("Codex_HomeInvalid"),
        _ => Localizer.Format("Codex_IntegrationOperationFailed", result.Code),
    };

    private string LocalizeRuntimeStatus(RuntimeServiceStatus status) => status switch
    {
        RuntimeServiceStatus.Running => Localizer.Get("Common_Running"),
        RuntimeServiceStatus.Stopped => Localizer.Get("Common_Stopped"),
        RuntimeServiceStatus.Available => Localizer.Get("Common_Available"),
        RuntimeServiceStatus.Unavailable => Localizer.Get("Common_Unavailable"),
        _ => Localizer.Get("Common_Error"),
    };

    private string LocalizeIntegrationStatus(CodexIntegrationState status) => status switch
    {
        CodexIntegrationState.Installed => Localizer.Get("Status_IntegrationInstalled"),
        CodexIntegrationState.Missing => Localizer.Get("Status_IntegrationMissing"),
        CodexIntegrationState.NeedsRepair => Localizer.Get("Status_IntegrationNeedsRepair"),
        CodexIntegrationState.NeedsManualReview => Localizer.Get("Status_IntegrationNeedsManualReview"),
        _ => Localizer.Get("Common_Unknown"),
    };

    private void ShowAbout()
    {
        MessageBox.Show(
            Localizer.Format(
                "About_Content",
                AgentBellProduct.InformationalVersion,
                AgentBellProtocol.ProtocolVersion),
            Localizer.Get("About_Title"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            MessageBox.Show(
                Localizer.Get("Error_OpenFolderFailed"),
                "AgentBell",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RecordUiResult(string resultCode)
    {
        _logger.Record(new DesktopDiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "tray",
            HttpStatusCode = 0,
            ElapsedMilliseconds = 0,
            PersistenceSucceeded = true,
            Result = resultCode,
        });
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs args)
    {
        _ = sender;
        _ = args;
        BeginExit();
    }

    private async void RunUiTask(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch
        {
            RecordUiResult("ui_action_failed");
            MessageBox.Show(
                Localizer.Get("Error_OperationFailed"),
                "AgentBell",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
