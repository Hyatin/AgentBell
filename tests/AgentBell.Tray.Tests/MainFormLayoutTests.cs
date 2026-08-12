using System.Globalization;
using System.Runtime.ExceptionServices;
using AgentBell.Localization;

namespace AgentBell.Tray.Tests;

public sealed class MainFormLayoutTests
{
    private static readonly int[] AcceptanceWidths = [860, 688, 1032];

    [Theory]
    [InlineData("en-US", 1.00f)]
    [InlineData("en-US", 1.25f)]
    [InlineData("en-US", 1.50f)]
    [InlineData("zh-CN", 1.00f)]
    [InlineData("zh-CN", 1.25f)]
    [InlineData("zh-CN", 1.50f)]
    public void BottomContent_DoesNotClipAcrossLanguageScaleAndWindowWidth(
        string language,
        float dpiScale)
    {
        RunOnStaThread(() => VerifyLayout(language, dpiScale));
    }

    private static void VerifyLayout(string language, float dpiScale)
    {
        var localizer = new AppLanguageService(
            language,
            () => CultureInfo.GetCultureInfo("en-US")).Localizer;
        var systemFont = SystemFonts.MessageBoxFont!;
        using var font = new Font(
            systemFont.FontFamily,
            systemFont.Size * dpiScale,
            systemFont.Style);
        using var form = new Form
        {
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(AcceptanceWidths[0], 420),
            Font = font,
            Location = new Point(-32000, -32000),
            Opacity = 0,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
        };
        var scrollHost = MainFormLayout.CreateScrollHost(out var content);
        form.Controls.Add(scrollHost);

        AddUpperContent(content);
        var settings = CreateSettingsGroup(localizer);
        MainFormLayout.AddAutoSizeRow(content, settings);

        var actionButtons = CreateActionButtons(localizer);
        var actions = MainFormLayout.CreateActionsGroup(
            localizer.Get("Main_Actions"),
            actionButtons,
            localizer.Get("Action_HookTrustNotice"));
        MainFormLayout.AddAutoSizeRow(content, actions);

        var hookNotice = actions.Controls.Find(
            MainFormLayout.ActionHookTrustNoticeName,
            true).OfType<Label>().Single();
        var buttonPanel = actions.Controls.Find(
            MainFormLayout.ActionsButtonPanelName,
            true).OfType<FlowLayoutPanel>().Single();
        var exitButton = actions.Controls.Find(
            MainFormLayout.ExitButtonName,
            true).OfType<Button>().Single();
        var betaNotice = MainFormLayout.CreateWrappingLabel(
            MainFormLayout.BetaSecurityNoticeName,
            localizer.Get("Main_BetaSecurityNotice"),
            Color.DarkSlateGray,
            new Padding(4, 12, 4, 4));
        MainFormLayout.AddAutoSizeRow(content, betaNotice);

        form.Show();
        foreach (var width in AcceptanceWidths)
        {
            form.ClientSize = new Size(width, 420);
            PumpLayout(scrollHost, content);
            AssertBottomLayoutFits(
                scrollHost,
                content,
                settings,
                actions,
                buttonPanel,
                exitButton,
                hookNotice,
                betaNotice,
                language,
                dpiScale,
                width);
        }

        if (language == "en-US")
        {
            form.ClientSize = new Size(1032, 420);
            PumpLayout(scrollHost, content);
            var wideHeight = actions.Height;
            var wideRows = VisibleButtonRows(buttonPanel);

            form.ClientSize = new Size(500, 420);
            PumpLayout(scrollHost, content);
            var narrowRows = VisibleButtonRows(buttonPanel);
            Assert.True(
                narrowRows > wideRows,
                $"English actions did not wrap into more rows: wide={wideRows}, " +
                $"narrow={narrowRows}.");
            Assert.True(
                actions.Height > wideHeight,
                $"Actions group did not grow after wrapping: wide={wideHeight}, " +
                $"narrow={actions.Height}.");
            AssertBottomLayoutFits(
                scrollHost,
                content,
                settings,
                actions,
                buttonPanel,
                exitButton,
                hookNotice,
                betaNotice,
                language,
                dpiScale,
                500);
        }

    }

    private static void AssertBottomLayoutFits(
        Panel scrollHost,
        TableLayoutPanel content,
        GroupBox settings,
        GroupBox actions,
        FlowLayoutPanel buttonPanel,
        Button exitButton,
        Label hookNotice,
        Label betaNotice,
        string language,
        float dpiScale,
        int width)
    {
        var scenario = $"language={language}, scale={dpiScale}, width={width}";
        Assert.True(actions.AutoSize, scenario);
        Assert.Equal(AutoSizeMode.GrowAndShrink, actions.AutoSizeMode);
        Assert.True(buttonPanel.AutoSize, scenario);
        Assert.Equal(AutoSizeMode.GrowAndShrink, buttonPanel.AutoSizeMode);
        Assert.True(buttonPanel.WrapContents, scenario);
        Assert.Equal(FlowDirection.LeftToRight, buttonPanel.FlowDirection);
        Assert.All(
            actions.Controls.OfType<TableLayoutPanel>().Single().RowStyles.Cast<RowStyle>(),
            row => Assert.Equal(SizeType.AutoSize, row.SizeType));

        AssertWrappingLabelFits(hookNotice, scenario);
        AssertWrappingLabelFits(betaNotice, scenario);
        var permissionHelp = settings.Controls.Find(
            "PermissionNotificationPolicyHelp",
            true).OfType<Label>().Single();
        AssertWrappingLabelFits(permissionHelp, scenario);
        AssertAncestorChainFits(permissionHelp, settings, scenario);
        AssertSubtreeFits(settings, scenario);
        AssertSubtreeFits(actions, scenario);
        AssertAncestorChainFits(exitButton, actions, scenario);
        AssertAncestorChainFits(hookNotice, actions, scenario);
        Assert.True(
            BoundsRelativeTo(exitButton, actions).Bottom < actions.ClientRectangle.Bottom,
            $"Exit button reaches the Actions border; {scenario}.");
        Assert.True(
            BoundsRelativeTo(hookNotice, actions).Bottom < actions.ClientRectangle.Bottom,
            $"Hook notice reaches the Actions border; {scenario}.");
        Assert.True(
            betaNotice.Bottom <= content.DisplayRectangle.Bottom,
            $"Beta notice exceeds content display rectangle; {scenario}.");
        Assert.True(
            actions.Bottom <= betaNotice.Top,
            $"Actions and beta notice overlap; {scenario}.");
        Assert.True(
            content.Height >= content.PreferredSize.Height,
            $"Content is shorter than its preferred height; {scenario}.");
        Assert.True(
            scrollHost.AutoScrollMinSize.Height >= content.Height,
            $"Scroll extent omits content height; {scenario}.");

        scrollHost.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
        var maximumScroll = Math.Max(
            0,
            scrollHost.VerticalScroll.Maximum -
            scrollHost.VerticalScroll.LargeChange +
            1);
        scrollHost.AutoScrollPosition = new Point(0, maximumScroll);
        Application.DoEvents();
        var visibleBetaBounds = BoundsRelativeTo(betaNotice, scrollHost);
        Assert.True(
            visibleBetaBounds.Bottom <= scrollHost.ClientRectangle.Bottom,
            $"Beta notice cannot be fully scrolled into view: beta={visibleBetaBounds}, " +
            $"viewport={scrollHost.ClientRectangle}, maxScroll={maximumScroll}; {scenario}.");
        Assert.True(
            scrollHost.ClientRectangle.Bottom - visibleBetaBounds.Bottom >= content.Padding.Bottom,
            $"Bottom padding is not scrollable: beta={visibleBetaBounds}, " +
            $"padding={content.Padding.Bottom}, viewport={scrollHost.ClientRectangle}; {scenario}.");
    }

    private static void AssertWrappingLabelFits(Label label, string scenario)
    {
        Assert.True(label.AutoSize, scenario);
        Assert.Equal(0, label.MaximumSize.Height);
        Assert.True(label.MaximumSize.Width > 0, scenario);
        Assert.True(
            label.PreferredSize.Height <= label.Height,
            $"{label.Name} is shorter than its wrapped text; {scenario}.");
        Assert.True(
            label.Right <= label.Parent!.DisplayRectangle.Right,
            $"{label.Name} is horizontally clipped; {scenario}.");
        Assert.True(
            label.Bottom <= label.Parent.DisplayRectangle.Bottom,
            $"{label.Name} is vertically clipped by its parent; {scenario}.");
    }

    private static void AssertSubtreeFits(Control parent, string scenario)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            Assert.True(
                child.Right <= parent.ClientRectangle.Right &&
                child.Bottom <= parent.ClientRectangle.Bottom,
                $"{child.Name}/{child.GetType().Name} bounds {child.Bounds} exceed " +
                $"{parent.Name}/{parent.GetType().Name} client " +
                $"{parent.ClientRectangle}; {scenario}.");
            AssertSubtreeFits(child, scenario);
        }
    }

    private static void AssertAncestorChainFits(
        Control control,
        Control ancestor,
        string scenario)
    {
        var current = control;
        while (current.Parent is { } parent)
        {
            Assert.True(
                current.Right <= parent.ClientRectangle.Right &&
                current.Bottom <= parent.ClientRectangle.Bottom,
                $"{current.Name}/{current.GetType().Name} is clipped by " +
                $"{parent.Name}/{parent.GetType().Name}; {scenario}.");
            if (parent == ancestor)
            {
                return;
            }

            current = parent;
        }

        Assert.Fail($"{control.Name} is not a descendant of {ancestor.Name}; {scenario}.");
    }

    private static GroupBox CreateSettingsGroup(IAppLocalizer localizer)
    {
        var settings = MainFormLayout.CreateGroup(localizer.Get("Settings_Title"));
        settings.Name = "SettingsGroup";
        var layout = MainFormLayout.CreateVerticalTable();
        layout.Name = "SettingsContent";
        foreach (var key in new[]
                 {
                     "Settings_NotifyTaskCompletion",
                     "Settings_NotifyActionRequired",
                     "Settings_NotifyReplyRequests",
                     "Settings_DetectQuestions",
                 })
        {
            MainFormLayout.AddAutoSizeRow(layout, new CheckBox
            {
                AutoSize = true,
                Text = localizer.Get(key),
            });
        }

        var permissionRow = new FlowLayoutPanel
        {
            Name = "PermissionNotificationPolicyRow",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 8),
        };
        permissionRow.Controls.Add(new Label
        {
            AutoSize = true,
            Text = localizer.Get("Settings_PermissionRequestNotifications"),
            Padding = new Padding(0, 7, 8, 0),
        });
        var policy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
        };
        policy.Items.AddRange(
        [
            localizer.Get("PermissionPolicy_Off"),
            localizer.Get("PermissionPolicy_AlwaysNotify"),
        ]);
        policy.SelectedIndex = 0;
        permissionRow.Controls.Add(policy);
        MainFormLayout.AddAutoSizeRow(layout, permissionRow);
        MainFormLayout.AddAutoSizeRow(
            layout,
            MainFormLayout.CreateWrappingLabel(
                "PermissionNotificationPolicyHelp",
                localizer.Get("Settings_PermissionRequestNotificationsHelp"),
                Color.DarkSlateGray,
                new Padding(0, 2, 0, 8)));

        settings.Controls.Add(layout);
        return settings;
    }

    private static Button[] CreateActionButtons(IAppLocalizer localizer) =>
        new[]
        {
            "Action_RepairCodexIntegration",
            "Action_ToggleStartup",
            "Action_OpenAndroidFolder",
            "Action_ExportDiagnostics",
            "Action_StopServices",
            "Action_StartServices",
            "Common_Exit",
        }.Select(key => new Button
        {
            Name = key == "Common_Exit" ? MainFormLayout.ExitButtonName : key,
            AutoSize = true,
            Text = localizer.Get(key),
            Margin = new Padding(4),
        }).ToArray();

    private static void AddUpperContent(TableLayoutPanel content)
    {
        var group = MainFormLayout.CreateGroup("Upper content");
        var layout = MainFormLayout.CreateVerticalTable();
        for (var index = 0; index < 14; index++)
        {
            MainFormLayout.AddAutoSizeRow(layout, new Label
            {
                AutoSize = true,
                Text = $"Status row {index + 1}",
                Padding = new Padding(0, 4, 0, 4),
            });
        }

        group.Controls.Add(layout);
        MainFormLayout.AddAutoSizeRow(content, group);
    }

    private static int VisibleButtonRows(FlowLayoutPanel panel) =>
        panel.Controls.Cast<Control>()
            .Where(control => control.Visible)
            .Select(control => control.Top)
            .Distinct()
            .Count();

    private static void PumpLayout(Panel scrollHost, TableLayoutPanel content)
    {
        scrollHost.AutoScrollPosition = Point.Empty;
        for (var pass = 0; pass < 3; pass++)
        {
            MainFormLayout.Reflow(scrollHost, content);
            Application.DoEvents();
        }
    }

    private static Rectangle BoundsRelativeTo(Control control, Control ancestor)
    {
        var bounds = control.Bounds;
        var parent = control.Parent;
        while (parent is not null && parent != ancestor)
        {
            bounds.Offset(parent.Left, parent.Top);
            parent = parent.Parent;
        }

        Assert.Same(ancestor, parent);
        return bounds;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(30)));
        thread.Join();
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
