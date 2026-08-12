using AgentBell.Desktop;
using AgentBell.Localization;

namespace AgentBell.Tray;

/// <summary>Provides the small localized status, pairing, settings, and action window.</summary>
public sealed class MainForm : Form
{
    private readonly TrayApplicationContext _context;
    private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _notificationChecks = new(StringComparer.Ordinal);
    private ComboBox _permissionPolicy = new();
    private Panel? _scrollHost;
    private TableLayoutPanel? _root;
    private PictureBox _pairingQr = new();
    private ListBox _eventHistory = new();
    private Label _eventDetails = new();
    private string? _pendingEventSelection;
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
        Resize += (_, _) => ReflowContent();
        DpiChanged += (_, _) => ReflowContent();
        FormClosed += (_, _) =>
        {
            var image = _pairingQr.Image;
            _pairingQr.Image = null;
            image?.Dispose();
        };
        Shown += (_, _) =>
        {
            ReflowContent();
            BeginRefresh();
        };
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

    /// <summary>Selects a sanitized history event after a notification click.</summary>
    public void SelectEvent(string eventId)
    {
        _pendingEventSelection = eventId;
        BeginRefresh();
    }

    private void BuildContent()
    {
        _rebuilding = true;
        try
        {
            var image = _pairingQr.Image;
            _pairingQr.Image = null;
            var previousScrollHost = _scrollHost;
            if (previousScrollHost is not null)
            {
                Controls.Remove(previousScrollHost);
                previousScrollHost.Dispose();
            }

            _pairingQr = new PictureBox { Image = image };
            _eventHistory = new ListBox();
            _eventDetails = new Label();
            _permissionPolicy = new ComboBox();
            _values.Clear();
            _notificationChecks.Clear();
            _scrollHost = MainFormLayout.CreateScrollHost(out var root);
            _root = root;
            Controls.Add(_scrollHost);

            MainFormLayout.AddAutoSizeRow(_root, CreateStatusGroup());
            MainFormLayout.AddAutoSizeRow(_root, CreateEventHistoryGroup());
            MainFormLayout.AddAutoSizeRow(_root, CreatePairingGroup());
            MainFormLayout.AddAutoSizeRow(_root, CreateSettingsGroup());
            MainFormLayout.AddAutoSizeRow(_root, CreateActionsGroup());
            MainFormLayout.AddAutoSizeRow(
                _root,
                MainFormLayout.CreateWrappingLabel(
                    MainFormLayout.BetaSecurityNoticeName,
                    Texts.Get("Main_BetaSecurityNotice"),
                    Color.DarkSlateGray,
                    new Padding(4, 12, 4, 4)));
            ReflowContent();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void ReflowContent()
    {
        MainFormLayout.Reflow(_scrollHost, _root);
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
        var layout = MainFormLayout.CreateVerticalTable();
        MainFormLayout.AddAutoSizeRow(layout, new Label
        {
            AutoSize = true,
            Text = Texts.Format("Pairing_ComputerName", Environment.MachineName),
        });
        _pairingQr.Size = new Size(280, 280);
        _pairingQr.SizeMode = PictureBoxSizeMode.Zoom;
        _pairingQr.BorderStyle = BorderStyle.FixedSingle;
        MainFormLayout.AddAutoSizeRow(layout, _pairingQr);
        MainFormLayout.AddAutoSizeRow(
            layout,
            MainFormLayout.CreateWrappingLabel(
                "PairingCredentialNotice",
                Texts.Get("Pairing_CredentialNotice")));
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
        };
        buttons.Controls.Add(CreateButton("Pairing_RegenerateQr", RegenerateQrAsync));
        buttons.Controls.Add(CreateButton("Pairing_CopyUrl", CopyPairingUrlAsync));
        MainFormLayout.AddAutoSizeRow(layout, buttons);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateSettingsGroup()
    {
        var group = CreateGroup("Settings_Title");
        var layout = MainFormLayout.CreateVerticalTable();
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
        MainFormLayout.AddAutoSizeRow(layout, row);
        AddNotificationCheck(
            layout,
            "notifyTaskCompletion",
            "Settings_NotifyTaskCompletion");
        AddNotificationCheck(
            layout,
            "notifyActionRequired",
            "Settings_NotifyActionRequired");
        AddPermissionNotificationPolicy(layout);
        AddNotificationCheck(
            layout,
            "notifyReplyRequests",
            "Settings_NotifyReplyRequests");
        AddNotificationCheck(
            layout,
            "detectQuestions",
            "Settings_DetectQuestions");
        group.Controls.Add(layout);
        return group;
    }

    private void AddPermissionNotificationPolicy(TableLayoutPanel parent)
    {
        var row = new FlowLayoutPanel
        {
            Name = "PermissionNotificationPolicyRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 8),
        };
        row.Controls.Add(new Label
        {
            AutoSize = true,
            Text = Texts.Get("Settings_PermissionRequestNotifications"),
            Padding = new Padding(0, 7, 8, 0),
        });
        _permissionPolicy = new ComboBox
        {
            Name = "PermissionNotificationPolicy",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
        };
        _permissionPolicy.Items.AddRange(
        [
            new PermissionPolicyOption(
                PermissionNotificationPolicy.Off,
                Texts.Get("PermissionPolicy_Off")),
            new PermissionPolicyOption(
                PermissionNotificationPolicy.AlwaysNotify,
                Texts.Get("PermissionPolicy_AlwaysNotify")),
        ]);
        _permissionPolicy.SelectedIndex = 0;
        _permissionPolicy.SelectedIndexChanged += (_, _) =>
        {
            if (!_rebuilding && !_refreshing)
            {
                RunUiTask(SaveNotificationSettingsAsync);
            }
        };
        row.Controls.Add(_permissionPolicy);
        MainFormLayout.AddAutoSizeRow(parent, row);
        MainFormLayout.AddAutoSizeRow(
            parent,
            MainFormLayout.CreateWrappingLabel(
                "PermissionNotificationPolicyHelp",
                Texts.Get("Settings_PermissionRequestNotificationsHelp"),
                Color.DarkSlateGray,
                new Padding(0, 2, 0, 8)));
    }

    private GroupBox CreateEventHistoryGroup()
    {
        var group = CreateGroup("EventHistory_Title");
        var layout = MainFormLayout.CreateVerticalTable();
        _eventHistory.Dock = DockStyle.Top;
        _eventHistory.Height = 140;
        _eventHistory.SelectedIndexChanged += (_, _) => UpdateSelectedEventDetails();
        _eventDetails = MainFormLayout.CreateWrappingLabel("EventDetails", string.Empty);
        MainFormLayout.AddAutoSizeRow(layout, _eventHistory);
        MainFormLayout.AddAutoSizeRow(layout, _eventDetails);
        group.Controls.Add(layout);
        return group;
    }

    private void AddNotificationCheck(
        TableLayoutPanel parent,
        string key,
        string resourceKey)
    {
        var checkBox = new CheckBox
        {
            AutoSize = true,
            Checked = true,
            Text = Texts.Get(resourceKey),
        };
        checkBox.CheckedChanged += (_, _) =>
        {
            if (!_rebuilding && !_refreshing)
            {
                RunUiTask(SaveNotificationSettingsAsync);
            }
        };
        _notificationChecks[key] = checkBox;
        MainFormLayout.AddAutoSizeRow(parent, checkBox);
    }

    private GroupBox CreateActionsGroup()
    {
        var buttons = new[]
        {
            CreateButton("Action_RepairCodexIntegration", _context.RepairIntegrationAsync),
            CreateButton("Action_ToggleStartup", ToggleStartupAsync),
            CreateButton("Action_OpenAndroidFolder", () =>
            {
                _context.OpenAndroidFolder();
                return Task.CompletedTask;
            }),
            CreateButton("Action_ExportDiagnostics", _context.ExportDiagnosticsAsync),
            CreateButton("Action_StopServices", _context.StopServicesAsync),
            CreateButton("Action_StartServices", _context.StartServicesAsync),
            CreateButton("Common_Exit", () =>
            {
                _allowClose = true;
                _context.BeginExit();
                return Task.CompletedTask;
            }),
        };
        return MainFormLayout.CreateActionsGroup(
            Texts.Get("Main_Actions"),
            buttons,
            Texts.Get("Action_HookTrustNotice"));
    }

    private GroupBox CreateGroup(string resourceKey) =>
        MainFormLayout.CreateGroup(Texts.Get(resourceKey));

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
        var value = MainFormLayout.CreateWrappingLabel(
            $"StatusValue_{key}",
            "—",
            padding: new Padding(0, 4, 0, 4));
        table.Controls.Add(value, 1, row);
        _values[key] = value;
    }

    private Button CreateButton(string textKey, Func<Task> action)
    {
        var button = new Button
        {
            Name = textKey == "Common_Exit" ? MainFormLayout.ExitButtonName : textKey,
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
            SetNotificationCheck("notifyTaskCompletion", snapshot.NotificationSettings.NotifyTaskCompletion);
            SetNotificationCheck("notifyActionRequired", snapshot.NotificationSettings.NotifyActionRequired);
            SetPermissionNotificationPolicy(
                snapshot.NotificationSettings.PermissionNotificationPolicy);
            SetNotificationCheck(
                "notifyReplyRequests",
                snapshot.NotificationSettings.NotifyReplyAndConfirmationRequests);
            SetNotificationCheck(
                "detectQuestions",
                snapshot.NotificationSettings.DetectQuestionsInCompletedResponses);
            RefreshEventHistory(snapshot.RecentEvents);
        }
        finally
        {
            _refreshing = false;
            ReflowContent();
        }
    }

    private Task SaveNotificationSettingsAsync() => _context.SetNotificationSettingsAsync(new()
    {
        NotifyTaskCompletion = IsNotificationChecked("notifyTaskCompletion"),
        NotifyActionRequired = IsNotificationChecked("notifyActionRequired"),
        PermissionNotificationPolicy = GetPermissionNotificationPolicy(),
        NotifyReplyAndConfirmationRequests = IsNotificationChecked("notifyReplyRequests"),
        DetectQuestionsInCompletedResponses = IsNotificationChecked("detectQuestions"),
    });

    private bool IsNotificationChecked(string key) =>
        _notificationChecks.TryGetValue(key, out var checkBox) && checkBox.Checked;

    private void SetNotificationCheck(string key, bool value)
    {
        if (_notificationChecks.TryGetValue(key, out var checkBox))
        {
            checkBox.Checked = value;
        }
    }

    private PermissionNotificationPolicy GetPermissionNotificationPolicy() =>
        (_permissionPolicy.SelectedItem as PermissionPolicyOption)?.Value
        ?? PermissionNotificationPolicy.Off;

    private void SetPermissionNotificationPolicy(PermissionNotificationPolicy value)
    {
        foreach (var item in _permissionPolicy.Items.OfType<PermissionPolicyOption>())
        {
            if (item.Value == value)
            {
                _permissionPolicy.SelectedItem = item;
                return;
            }
        }

        _permissionPolicy.SelectedIndex = 0;
    }

    private void RefreshEventHistory(IReadOnlyList<AgentBell.Contracts.AgentEvent> events)
    {
        var selectedId = _pendingEventSelection
            ?? (_eventHistory.SelectedItem as EventHistoryItem)?.Event.EventId;
        _eventHistory.BeginUpdate();
        try
        {
            _eventHistory.Items.Clear();
            foreach (var agentEvent in events
                .Where(item => item.ResolvedAt is null)
                .OrderByDescending(item => item.Sequence))
            {
                _eventHistory.Items.Add(new EventHistoryItem(agentEvent, EventDisplayText(agentEvent)));
            }

            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                for (var index = 0; index < _eventHistory.Items.Count; index++)
                {
                    if (_eventHistory.Items[index] is EventHistoryItem item
                        && item.Event.EventId == selectedId)
                    {
                        _eventHistory.SelectedIndex = index;
                        break;
                    }
                }
            }
        }
        finally
        {
            _eventHistory.EndUpdate();
            _pendingEventSelection = null;
        }

        UpdateSelectedEventDetails();
    }

    private string EventDisplayText(AgentBell.Contracts.AgentEvent agentEvent) =>
        Texts.Format(
            "EventHistory_Row",
            EventTypeText(agentEvent),
            string.IsNullOrWhiteSpace(agentEvent.Project) ? "—" : agentEvent.Project,
            agentEvent.OccurredAt.ToLocalTime().ToString("g"));

    private string EventTypeText(AgentBell.Contracts.AgentEvent agentEvent) => agentEvent.ActionType switch
    {
        AgentBell.Contracts.AgentActionTypes.PermissionRequired => Texts.Get("EventType_PermissionRequired"),
        AgentBell.Contracts.AgentActionTypes.InputRequired => Texts.Get("EventType_InputRequired"),
        AgentBell.Contracts.AgentActionTypes.ConfirmationRequired => Texts.Get("EventType_ConfirmationRequired"),
        AgentBell.Contracts.AgentActionTypes.AttentionRequired => Texts.Get("EventType_AttentionRequired"),
        _ => Texts.Get("EventType_Completed"),
    };

    private void UpdateSelectedEventDetails()
    {
        _eventDetails.Text = _eventHistory.SelectedItem is EventHistoryItem item
            ? Texts.Format(
                "EventHistory_Details",
                EventTypeText(item.Event),
                string.IsNullOrWhiteSpace(item.Event.Project) ? "—" : item.Event.Project,
                item.Event.OccurredAt.ToLocalTime().ToString("F"))
            : Texts.Get("EventHistory_NoneSelected");
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

    private sealed record PermissionPolicyOption(
        PermissionNotificationPolicy Value,
        string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record EventHistoryItem(
        AgentBell.Contracts.AgentEvent Event,
        string Label)
    {
        public override string ToString() => Label;
    }
}
