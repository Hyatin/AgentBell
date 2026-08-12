namespace AgentBell.Tray;

/// <summary>Provides content-driven WinForms layout primitives for the main window.</summary>
internal static class MainFormLayout
{
    internal const string ActionsGroupName = "ActionsGroup";
    internal const string ActionsButtonPanelName = "ActionsButtonPanel";
    internal const string ExitButtonName = "ExitButton";
    internal const string ActionHookTrustNoticeName = "ActionHookTrustNotice";
    internal const string BetaSecurityNoticeName = "MainBetaSecurityNotice";

    internal static Panel CreateScrollHost(out TableLayoutPanel content)
    {
        var contentPanel = CreateVerticalTable();
        contentPanel.Name = "MainContent";
        contentPanel.Padding = new Padding(16);
        content = contentPanel;

        var host = new Panel
        {
            Name = "MainScrollHost",
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };
        host.Controls.Add(contentPanel);
        host.ClientSizeChanged += (_, _) =>
        {
            RefreshWrappingLabels(contentPanel);
            UpdateScrollExtent(host, contentPanel);
        };
        contentPanel.SizeChanged += (_, _) => UpdateScrollExtent(host, contentPanel);
        return host;
    }

    internal static TableLayoutPanel CreateVerticalTable() => new TableLayoutPanel
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        RowCount = 0,
        Margin = Padding.Empty,
    }.WithSinglePercentColumn();

    internal static GroupBox CreateGroup(string text) => new ContentDrivenGroupBox
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12),
    };

    internal static GroupBox CreateActionsGroup(
        string title,
        IEnumerable<Button> actionButtons,
        string hookNoticeText)
    {
        ArgumentNullException.ThrowIfNull(actionButtons);

        var group = CreateGroup(title);
        group.Name = ActionsGroupName;
        var layout = CreateVerticalTable();
        layout.Name = "ActionsContent";
        var buttons = new FlowLayoutPanel
        {
            Name = ActionsButtonPanelName,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty,
        };
        foreach (var button in actionButtons)
        {
            buttons.Controls.Add(button);
        }

        AddAutoSizeRow(layout, buttons);
        AddAutoSizeRow(
            layout,
            CreateWrappingLabel(ActionHookTrustNoticeName, hookNoticeText));
        group.Controls.Add(layout);
        return group;
    }

    internal static Label CreateWrappingLabel(
        string name,
        string text,
        Color? foreColor = null,
        Padding? padding = null)
    {
        var label = new AutoWrappingLabel
        {
            Name = name,
            Text = text,
            Dock = DockStyle.Top,
            Padding = padding ?? Padding.Empty,
        };
        if (foreColor is not null)
        {
            label.ForeColor = foreColor.Value;
        }

        return label;
    }

    internal static void AddAutoSizeRow(TableLayoutPanel table, Control control)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(control);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }

    internal static void RefreshWrappingLabels(Control? root)
    {
        if (root is null)
        {
            return;
        }

        foreach (var label in Descendants(root).OfType<AutoWrappingLabel>())
        {
            label.RefreshWrappingWidth();
        }

        root.PerformLayout();
    }

    internal static void Reflow(Panel? scrollHost, TableLayoutPanel? content)
    {
        if (scrollHost is null || content is null)
        {
            return;
        }

        scrollHost.PerformLayout();
        RefreshWrappingLabels(content);
        foreach (var control in Descendants(content).Reverse())
        {
            control.PerformLayout();
        }

        content.PerformLayout();
        scrollHost.PerformLayout();
        UpdateScrollExtent(scrollHost, content);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void UpdateScrollExtent(Panel host, TableLayoutPanel content)
    {
        var contentHeight = Math.Max(content.Height, content.PreferredSize.Height);
        host.AutoScrollMinSize = new Size(
            0,
            contentHeight + content.Margin.Vertical);
    }

    private static TableLayoutPanel WithSinglePercentColumn(this TableLayoutPanel table)
    {
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private sealed class AutoWrappingLabel : Label
    {
        private Control? _observedParent;
        private bool _refreshing;

        public AutoWrappingLabel()
        {
            AutoSize = true;
            AutoEllipsis = false;
            UseMnemonic = false;
        }

        internal void RefreshWrappingWidth()
        {
            if (_refreshing || Parent is null)
            {
                return;
            }

            var availableWidth = AvailableWidth(Parent);
            if (availableWidth <= 0 || MaximumSize.Width == availableWidth)
            {
                return;
            }

            _refreshing = true;
            try
            {
                MaximumSize = new Size(availableWidth, 0);
            }
            finally
            {
                _refreshing = false;
            }
        }

        protected override void OnParentChanged(EventArgs e)
        {
            if (_observedParent is not null)
            {
                _observedParent.ClientSizeChanged -= OnParentLayoutChanged;
                _observedParent.Layout -= OnParentLayoutChanged;
            }

            base.OnParentChanged(e);
            _observedParent = Parent;
            if (_observedParent is not null)
            {
                _observedParent.ClientSizeChanged += OnParentLayoutChanged;
                _observedParent.Layout += OnParentLayoutChanged;
            }

            RefreshWrappingWidth();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RefreshWrappingWidth();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            RefreshWrappingWidth();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _observedParent is not null)
            {
                _observedParent.ClientSizeChanged -= OnParentLayoutChanged;
                _observedParent.Layout -= OnParentLayoutChanged;
                _observedParent = null;
            }

            base.Dispose(disposing);
        }

        private int AvailableWidth(Control parent)
        {
            var width = parent.DisplayRectangle.Width - Margin.Horizontal;
            if (parent is not TableLayoutPanel table)
            {
                return Math.Max(1, width);
            }

            var position = table.GetPositionFromControl(this);
            var columnWidths = table.GetColumnWidths();
            return position.Column >= 0
                && position.Column < columnWidths.Length
                && columnWidths[position.Column] > 0
                    ? Math.Max(1, columnWidths[position.Column] - Margin.Horizontal)
                    : Math.Max(1, width);
        }

        private void OnParentLayoutChanged(object? sender, EventArgs e) =>
            RefreshWrappingWidth();
    }

    private sealed class ContentDrivenGroupBox : GroupBox
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var preferred = base.GetPreferredSize(proposedSize);
            if (Controls.Count == 0)
            {
                return preferred;
            }

            var bottomInset = Math.Max(
                Padding.Bottom,
                Math.Max(0, ClientSize.Height - DisplayRectangle.Bottom));
            var requiredHeight = Controls
                .Cast<Control>()
                .Where(control => control.Visible)
                .Select(control => control.Bottom + control.Margin.Bottom + bottomInset)
                .DefaultIfEmpty(preferred.Height)
                .Max();
            return new Size(preferred.Width, Math.Max(preferred.Height, requiredHeight));
        }

    }
}
