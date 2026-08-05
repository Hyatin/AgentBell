using System.Diagnostics;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Integration;
using Microsoft.Win32;

namespace AgentBell.Tray;

/// <summary>Owns the NotifyIcon, shared runtime, integration actions, and graceful shutdown.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _serviceStatusItem;
    private readonly ToolStripMenuItem _clientCountItem;
    private readonly ToolStripMenuItem _recentEventItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly AgentBellRuntime _runtime;
    private readonly IntegrationService _integration;
    private readonly StartupRegistration _startup;
    private readonly string _androidApkPath;
    private readonly string _dataDirectory;
    private readonly IDesktopDiagnosticLogger _logger;
    private readonly object _exitGate = new();

    private MainForm? _mainForm;
    private Task _startupTask;
    private bool _exiting;

    /// <summary>Initializes the per-user Tray context with production paths.</summary>
    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var pathResolver = new AgentBellPathResolver();
        var runtimeOptions = DesktopRuntimeOptions.CreateDefault(pathResolver);
        _dataDirectory = Path.GetDirectoryName(runtimeOptions.EventsFilePath)
            ?? throw new InvalidOperationException("AgentBell data path is unavailable.");
        _androidApkPath = pathResolver.AndroidApkPath;
        _logger = new RollingDesktopDiagnosticLogger(
            runtimeOptions.DiagnosticLogPath
                ?? Path.Combine(_dataDirectory, "logs", "tray.ndjson"));
        _runtime = new AgentBellRuntime(runtimeOptions, _logger);
        _integration = new IntegrationService(
            pathResolver.GetInstalledExecutablePath("AgentBell.Hook.exe"));
        _startup = new StartupRegistration(
            pathResolver.GetInstalledExecutablePath("AgentBell.Tray.exe"));

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 AgentBell", null, (_, _) => ShowMainWindow());
        menu.Items.Add("显示配对二维码", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        _serviceStatusItem = new ToolStripMenuItem("连接状态：正在启动") { Enabled = false };
        _clientCountItem = new ToolStripMenuItem("手机连接数：0") { Enabled = false };
        _recentEventItem = new ToolStripMenuItem("最近完成事件：—") { Enabled = false };
        menu.Items.Add(_serviceStatusItem);
        menu.Items.Add(_clientCountItem);
        menu.Items.Add(_recentEventItem);
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("开机自动启动");
        _startupItem.Click += (_, _) => RunUiTask(ToggleStartupAsync);
        menu.Items.Add(_startupItem);
        menu.Items.Add("安装/修复 Codex 集成", null, (_, _) => RunUiTask(RepairIntegrationAsync));
        menu.Items.Add("检查 Codex 集成", null, (_, _) => RunUiTask(CheckIntegrationAsync));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开 Android APK 所在文件夹", null, (_, _) => OpenAndroidFolder());
        menu.Items.Add("打开数据目录", null, (_, _) => OpenFolder(_dataDirectory));
        menu.Items.Add("导出脱敏诊断", null, (_, _) => RunUiTask(ExportDiagnosticsAsync));
        menu.Items.Add("关于", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => BeginExit());

        _notifyIcon = new NotifyIcon
        {
            Text = "AgentBell",
            Icon = SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

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

    /// <summary>Returns current integration status.</summary>
    public Task<CodexIntegrationResult> GetIntegrationStatusAsync(CancellationToken cancellationToken) =>
        _integration.ExecuteAsync("status", cancellationToken);

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
                "本地接收服务无法启动。请检查 127.0.0.1:17863 是否被其他程序占用，并导出诊断。",
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
        var text = result.Success
            ? "Codex 集成已安装。Codex 将要求审核新的稳定 Hook 路径；请确认路径属于 AgentBell 后选择信任。"
            : UserFacingIntegrationError(result);
        MessageBox.Show(
            text,
            "AgentBell Codex 集成",
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
            Filter = "ZIP archive (*.zip)|*.zip",
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
            result.Success ? "脱敏诊断包已导出并通过敏感内容扫描。" : $"诊断导出失败：{result.Code}",
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
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
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
        _serviceStatusItem.Text = $"连接状态：Hook {snapshot.LocalHookService} / LAN {snapshot.LanService}";
        _clientCountItem.Text = $"手机连接数：{snapshot.WebSocketClientCount}";
        _recentEventItem.Text = snapshot.LastEventTime is null
            ? "最近完成事件：—"
            : $"最近完成事件：sequence {snapshot.LatestSequence} · {snapshot.LastEventTime.Value.ToLocalTime():HH:mm:ss}";
        _startupItem.Checked = _startup.Status().State == StartupRegistrationState.Enabled;
    }

    private Task ToggleStartupAsync()
    {
        var current = _startup.Status();
        var result = current.State == StartupRegistrationState.Enabled
            ? _startup.Disable()
            : _startup.Enable();
        if (!result.Success)
        {
            MessageBox.Show("无法更新当前用户的开机启动项。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return RefreshMenuAsync();
    }

    private async Task CheckIntegrationAsync()
    {
        var result = await GetIntegrationStatusAsync(CancellationToken.None).ConfigureAwait(true);
        MessageBox.Show(
            result.Success ? $"Codex 集成状态：{result.State}" : UserFacingIntegrationError(result),
            "AgentBell Codex 集成",
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

    private static string UserFacingIntegrationError(CodexIntegrationResult result) =>
        result.Code switch
        {
            "hooks_json_invalid" or "hooks_root_invalid" or "hooks_structure_invalid" =>
                $"hooks.json 无效，AgentBell 未覆盖文件。请手工检查：{result.HooksPath}",
            "manual_review_required" =>
                $"发现多个或非标准 AgentBell Hook。为避免误删，请手工检查：{result.HooksPath}",
            "hook_executable_missing" => "稳定安装目录中的 AgentBell.Hook.exe 不存在，需要重新安装。",
            "codex_home_invalid" => "CODEX_HOME 无效。",
            _ => $"Codex 集成操作失败：{result.Code}",
        };

    private void ShowAbout()
    {
        MessageBox.Show(
            $"AgentBell {AgentBellProduct.InformationalVersion}\nProtocol {AgentBellProtocol.ProtocolVersion}\n\n可信局域网 HTTP/WS；不是端到端加密。\n无云端、无自动更新服务。",
            "关于 AgentBell",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void OpenFolder(string path)
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
            MessageBox.Show("无法打开目录。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show("操作失败。可导出脱敏诊断进一步检查。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
