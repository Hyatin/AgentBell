using AgentBell.Desktop;
using AgentBell.Localization;

namespace AgentBell.Tray;

/// <summary>Provides the small localized status, pairing, settings, and action window.</summary>
public sealed class MainForm : Form
{
    private readonly TrayApplicationContext _context;
    private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);
    private TableLayoutPanel? _root;
    private PictureBox _pairingQr = new();
    private bool _refreshing;
    private bool _rebuilding;
    private bool _allowClose;

    /// <summary>Initializes the main window without rendering a token as text.</summary>
    public MainForm(TrayApplicationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Text = "AgentBell";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 760);
        Size = new Size(860, 840);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildContent();

        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            var image = _pairingQr.Image;
            _pairingQr.Image = null;
            image?.Dispose();
        };
        Shown += (_, _) => BeginRefresh();
    }

    private IAppLocalizer Texts => _context.Localizer;

    /// <summary>Rebuilds all visible controls after a language change.</summary>
    public void ApplyLanguage()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ApplyLanguage);
            return;
        }

        BuildContent();
        BeginRefresh();
    }

    /// <summary>Schedules a safe UI refresh.</summary>
    public void BeginRefresh() => RunUiTask(RefreshAsync);

    private void BuildContent()
    {
        _rebuilding = true;
        try
        {
            var image = _pairingQr.Image;
            _pairingQr.Image = null;
            var previousRoot = _root;
            if (previousRoot is not null)
            {
                Controls.Remove(previousRoot);
                previousRoot.Dispose();
            }

            _pairingQr = new PictureBox { Image = image };
            _values.Clear();
            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(16),
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_root);

            _root.Controls.Add(CreateStatusGroup());
            _root.Controls.Add(CreatePairingGroup());
            _root.Controls.Add(CreateSettingsGroup());
            _root.Controls.Add(CreateActionsGroup());
            _root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                Text = Texts.Get("Main_BetaSecurityNotice"),
                ForeColor = Color.DarkSlateGray,
                Padding = new Padding(4, 12, 4, 4),
            });
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private GroupBox CreateStatusGroup()
    {
        var group = CreateGroup("Main_Status");
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatusRow(table, "version", "Status_Version");
        AddStatusRow(table, "hook", "Status_LocalHookService");
        AddStatusRow(table, "lan", "Status_LanService");
        AddStatusRow(table, "endpoint", "Status_LanEndpoint");
        AddStatusRow(table, "clients", "Status_PhoneConnections");
        AddStatusRow(table, "lastEvent", "Status_LastEvent");
        AddStatusRow(table, "sequence", "Status_LatestSequence");
        AddStatusRow(table, "startup", "Status_StartWithWindows");
        AddStatusRow(table, "integration", "Status_CodexIntegration");
        AddStatusRow(table, "apk", "Status_AndroidApk");
        group.Controls.Add(table);
        return group;
    }

    private GroupBox CreatePairingGroup()
    {
        var group = CreateGroup("Main_Pairing");
        var layout = CreateVerticalFlow();
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = Texts.Format("Pairing_ComputerName", Environment.MachineName),
        });
        _pairingQr.Size = new Size(280, 280);
        _pairingQr.SizeMode = PictureBoxSizeMode.Zoom;
        _pairingQr.BorderStyle = BorderStyle.FixedSingle;
        layout.Controls.Add(_pairingQr);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = Texts.Get("Pairing_CredentialNotice"),
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.Add(CreateButton("Pairing_RegenerateQr", RegenerateQrAsync));
        buttons.Controls.Add(CreateButton("Pairing_CopyUrl", CopyPairingUrlAsync));
        layout.Controls.Add(buttons);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateSettingsGroup()
    {
        var group = CreateGroup("Settings_Title");
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
        };
        row.Controls.Add(new Label
        {
            AutoSize = true,
            Text = Texts.Get("Settings_Language"),
            Padding = new Padding(0, 7, 8, 0),
        });
        var language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
        };
        var options = new[]
        {
            new LanguageOption(AppLanguage.System, Texts.Get("Language_System")),
            new LanguageOption(AppLanguage.English, Texts.Get("Language_English")),
            new LanguageOption(AppLanguage.ChineseSimplified, Texts.Get("Language_ChineseSimplified")),
        };
        language.Items.AddRange(options);
        language.SelectedItem = options.Single(option => option.Value == _context.Language.Current);
        language.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding && language.SelectedItem is LanguageOption selected)
            {
                RunUiTask(() => _context.SetLanguageAsync(selected.Value));
            }
        };
        row.Controls.Add(language);
        group.Controls.Add(row);
        return group;
    }

    private GroupBox CreateActionsGroup()
    {
        var group = CreateGroup("Main_Actions");
        var layout = CreateVerticalFlow();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
        };
        buttons.Controls.Add(CreateButton("Action_RepairCodexIntegration", _context.RepairIntegrationAsync));
        buttons.Controls.Add(CreateButton("Action_ToggleStartup", ToggleStartupAsync));
        buttons.Controls.Add(CreateButton("Action_OpenAndroidFolder", () =>
        {
            _context.OpenAndroidFolder();
            return Task.CompletedTask;
        }));
        buttons.Controls.Add(CreateButton("Action_ExportDiagnostics", _context.ExportDiagnosticsAsync));
        buttons.Controls.Add(CreateButton("Action_StopServices", _context.StopServicesAsync));
        buttons.Controls.Add(CreateButton("Action_StartServices", _context.StartServicesAsync));
        buttons.Controls.Add(CreateButton("Common_Exit", () =>
        {
            _allowClose = true;
            _context.BeginExit();
            return Task.CompletedTask;
        }));
        layout.Controls.Add(buttons);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = Texts.Get("Action_HookTrustNotice"),
        });
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateGroup(string resourceKey) => new()
    {
        Text = Texts.Get(resourceKey),
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(12),
    };

    private static FlowLayoutPanel CreateVerticalFlow() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
    };

    private void AddStatusRow(TableLayoutPanel table, string key, string captionKey)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            AutoSize = true,
            Text = Texts.Get(captionKey),
            Padding = new Padding(0, 4, 8, 4),
        }, 0, row);
        var value = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(550, 0),
            Text = "—",
            Padding = new Padding(0, 4, 0, 4),
        };
        table.Controls.Add(value, 1, row);
        _values[key] = value;
    }

    private Button CreateButton(string textKey, Func<Task> action)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = Texts.Get(textKey),
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
                _context.AndroidApkPath,
                Texts);
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
            ShowWarning(Texts.Get("Pairing_NoLanAddress"));
        }
    }

    private Task CopyPairingUrlAsync()
    {
        var url = _context.Runtime.GetPairingUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowWarning(Texts.Get("Pairing_NoUrl"));
            return Task.CompletedTask;
        }

        var confirmation = MessageBox.Show(
            PairingUrlDisclosurePolicy.WarningText(Texts),
            Texts.Get("Pairing_CopySensitiveUrlTitle"),
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
            ShowWarning(Texts.Get("Error_StartupUpdateFailed"));
        }

        return Task.CompletedTask;
    }

    private void ShowWarning(string message) => MessageBox.Show(
        message,
        "AgentBell",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);

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
            ShowWarning(Texts.Get("Error_OperationFailed"));
        }
    }

    private sealed record LanguageOption(AppLanguage Value, string Label)
    {
        public override string ToString() => Label;
    }
}
