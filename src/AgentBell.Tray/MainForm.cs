using AgentBell.Desktop;

namespace AgentBell.Tray;

/// <summary>Provides the intentionally small M4 status, pairing, and action window.</summary>
public sealed class MainForm : Form
{
    private readonly TrayApplicationContext _context;
    private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);
    private readonly PictureBox _pairingQr = new();
    private readonly Label _serviceHint = new();
    private bool _refreshing;
    private bool _allowClose;

    /// <summary>Initializes the main window without rendering a token as text.</summary>
    public MainForm(TrayApplicationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Text = "AgentBell";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 720);
        Size = new Size(780, 780);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(CreateStatusGroup());
        root.Controls.Add(CreatePairingGroup());
        root.Controls.Add(CreateActionsGroup());
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "当前为开发测试版 APK，Android 可能提示未知来源安装。M4 使用可信局域网 HTTP/WS，不是端到端加密；Windows 防火墙仅允许专用网络。Codex Hook 信任确认不可自动绕过。",
            ForeColor = Color.DarkSlateGray,
            Padding = new Padding(4, 12, 4, 4),
        });

        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            var image = _pairingQr.Image;
            _pairingQr.Image = null;
            image?.Dispose();
        };
        Shown += (_, _) => BeginRefresh();
    }

    /// <summary>Schedules a safe UI refresh.</summary>
    public void BeginRefresh()
    {
        RunUiTask(RefreshAsync);
    }

    private GroupBox CreateStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "状态",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatusRow(table, "version", "AgentBell 版本");
        AddStatusRow(table, "hook", "本地 Hook 服务");
        AddStatusRow(table, "lan", "LAN 服务");
        AddStatusRow(table, "endpoint", "LAN 地址和端口");
        AddStatusRow(table, "clients", "手机连接数");
        AddStatusRow(table, "lastEvent", "最近一次事件");
        AddStatusRow(table, "sequence", "最新 sequence");
        AddStatusRow(table, "startup", "开机启动");
        AddStatusRow(table, "integration", "Codex 集成");
        AddStatusRow(table, "apk", "Android APK");
        group.Controls.Add(table);
        return group;
    }

    private GroupBox CreatePairingGroup()
    {
        var group = new GroupBox
        {
            Text = "配对",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
        };
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"电脑名称：{Environment.MachineName}",
        });
        _pairingQr.Size = new Size(280, 280);
        _pairingQr.SizeMode = PictureBoxSizeMode.Zoom;
        _pairingQr.BorderStyle = BorderStyle.FixedSingle;
        layout.Controls.Add(_pairingQr);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Text = "二维码包含配对凭据。窗口不会以明文显示 Token；关闭窗口也不会删除 Token 或要求手机重新配对。",
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.Add(CreateButton("重新生成二维码", () => RegenerateQrAsync()));
        buttons.Controls.Add(CreateButton("复制配对 URL", () => CopyPairingUrlAsync()));
        layout.Controls.Add(buttons);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateActionsGroup()
    {
        var group = new GroupBox
        {
            Text = "操作",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
        };
        buttons.Controls.Add(CreateButton("安装/修复 Codex 集成", _context.RepairIntegrationAsync));
        buttons.Controls.Add(CreateButton("启用/关闭开机启动", ToggleStartupAsync));
        buttons.Controls.Add(CreateButton("打开 Android APK 位置", () =>
        {
            _context.OpenAndroidFolder();
            return Task.CompletedTask;
        }));
        buttons.Controls.Add(CreateButton("导出诊断包", _context.ExportDiagnosticsAsync));
        buttons.Controls.Add(CreateButton("停止接收服务", _context.StopServicesAsync));
        buttons.Controls.Add(CreateButton("启动接收服务", _context.StartServicesAsync));
        buttons.Controls.Add(CreateButton("退出", () =>
        {
            _allowClose = true;
            _context.BeginExit();
            return Task.CompletedTask;
        }));
        _serviceHint.AutoSize = true;
        _serviceHint.MaximumSize = new Size(680, 0);
        _serviceHint.Text = "更改 Hook 路径后，Codex 可能要求重新审核。请确认稳定路径属于 AgentBell 后再选择信任。";
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        layout.Controls.Add(buttons);
        layout.Controls.Add(_serviceHint);
        group.Controls.Add(layout);
        return group;
    }

    private void AddStatusRow(TableLayoutPanel table, string key, string caption)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            AutoSize = true,
            Text = caption,
            Padding = new Padding(0, 4, 8, 4),
        }, 0, row);
        var value = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Text = "—",
            Padding = new Padding(0, 4, 0, 4),
        };
        table.Controls.Add(value, 1, row);
        _values[key] = value;
    }

    private Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(4),
        };
        button.Click += (_, _) => RunUiTask(async () =>
        {
            await action().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        });
        return button;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var snapshot = await _context.Runtime
                .GetSnapshotAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var integration = await _context
                .GetIntegrationStatusAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var projection = TrayStatusProjection.Create(
                snapshot,
                integration,
                _context.Startup.Status(),
                _context.AndroidApkPath);
            foreach (var item in projection)
            {
                if (_values.TryGetValue(item.Key, out var label))
                {
                    label.Text = item.Value;
                }
            }

            LoadQr(snapshot.PairingQrAvailable ? snapshot.PairingQrCodePath : null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RegenerateQrAsync()
    {
        var success = await _context.Runtime
            .RegeneratePairingQrAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (!success)
        {
            MessageBox.Show("当前没有可用的 LAN 配对地址。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private Task CopyPairingUrlAsync()
    {
        var url = _context.Runtime.GetPairingUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("当前没有可用的配对 URL。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        var confirmation = MessageBox.Show(
            PairingUrlDisclosurePolicy.WarningText,
            "复制敏感配对 URL",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation == DialogResult.Yes)
        {
            Clipboard.SetText(url);
        }

        return Task.CompletedTask;
    }

    private Task ToggleStartupAsync()
    {
        var current = _context.Startup.Status();
        var result = current.State == StartupRegistrationState.Enabled
            ? _context.Startup.Disable()
            : _context.Startup.Enable();
        if (!result.Success)
        {
            MessageBox.Show("开机启动项写入失败。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return Task.CompletedTask;
    }

    private void LoadQr(string? path)
    {
        Image? replacement = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var source = Image.FromStream(stream);
                replacement = new Bitmap(source);
            }
        }
        catch
        {
            replacement?.Dispose();
            replacement = null;
        }

        var previous = _pairingQr.Image;
        _pairingQr.Image = replacement;
        previous?.Dispose();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        _ = sender;
        if (!_allowClose && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Hide();
        }
    }

    private async void RunUiTask(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch
        {
            MessageBox.Show("操作失败。可导出脱敏诊断进一步检查。", "AgentBell", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
