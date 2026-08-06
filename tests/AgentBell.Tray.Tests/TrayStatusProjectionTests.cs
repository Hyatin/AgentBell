using System.Text.Json;
using AgentBell.Desktop;
using AgentBell.Integration;
using AgentBell.Localization;

namespace AgentBell.Tray.Tests;

public sealed class TrayStatusProjectionTests
{
    [Fact]
    public void Projection_ShowsRequiredStateWithoutPairingTokenOrUrl()
    {
        var snapshot = new AgentBellRuntimeSnapshot
        {
            LocalHookService = RuntimeServiceStatus.Running,
            LanService = RuntimeServiceStatus.Available,
            LocalResultCode = "running",
            LanResultCode = "available",
            LanAddress = "192.168.1.20",
            LanPort = 17864,
            WebSocketClientCount = 2,
            LatestSequence = 42,
            EventCount = 7,
            LastEventTime = DateTimeOffset.Parse("2026-08-03T12:00:00+08:00"),
            PairingQrCodePath = "C:\\data\\pairing.png",
            PairingQrAvailable = true,
        };
        var integration = new CodexIntegrationResult
        {
            Success = true,
            Changed = false,
            State = CodexIntegrationState.Installed,
            Code = "installed",
            AgentBellHookCount = 1,
            TrustReviewRequired = false,
        };

        var result = TrayStatusProjection.Create(
            snapshot,
            integration,
            new StartupRegistrationResult(true, StartupRegistrationState.Enabled, "enabled"),
            "C:\\AgentBell\\android\\AgentBell-Android-0.6.0-beta.1.apk",
            EnglishLocalizer());
        var json = JsonSerializer.Serialize(result);

        Assert.Equal("Running", result["hook"]);
        Assert.Equal("0.6.0-beta.1", result["version"]);
        Assert.Equal("Installed", result["integration"]);
        Assert.Equal("2", result["clients"]);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingUrlPolicy_AlwaysRequiresExplicitConfirmation()
    {
        Assert.True(PairingUrlDisclosurePolicy.RequiresConfirmation);
        var chinese = new AppLanguageService(
            "zh-CN",
            () => System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var warning = PairingUrlDisclosurePolicy.WarningText(chinese.Localizer);
        Assert.Contains("配对凭据", warning, StringComparison.Ordinal);
        Assert.Contains("可信局域网", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsNotification_CanBeGeneratedInBothLanguagesWithoutEventContent()
    {
        var english = WindowsNotificationProjection.Create(EnglishLocalizer());
        var chinese = WindowsNotificationProjection.Create(new AppLanguageService(
            "zh-CN",
            () => System.Globalization.CultureInfo.GetCultureInfo("en-US")).Localizer);

        Assert.Equal("Codex task completed", english.Title);
        Assert.Equal("AgentBell received a completion event.", english.Body);
        Assert.Equal("Codex 任务已完成", chinese.Title);
        Assert.Equal("AgentBell 已收到完成事件。", chinese.Body);
        Assert.DoesNotContain("summary", english.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn", english.Body, StringComparison.OrdinalIgnoreCase);
    }

    private static IAppLocalizer EnglishLocalizer() => new AppLanguageService(
        "en-US",
        () => System.Globalization.CultureInfo.GetCultureInfo("zh-CN")).Localizer;
}
