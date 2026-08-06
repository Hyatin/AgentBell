using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Localization;

namespace AgentBell.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var pathResolver = new AgentBellPathResolver();
        var runtimeOptions = DesktopRuntimeOptions.CreateDefault(pathResolver);
        var languagePreference = AppLanguagePreferenceReader.ReadWithStatus(
            runtimeOptions.ConfigFilePath);
        var languageService = new AppLanguageService(languagePreference.Value);
        ApplicationConfiguration.Initialize();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var coordinator = new SingleInstanceCoordinator();
        var shutdownRequested = TrayLaunchPolicy.IsShutdownRequest(args);
        if (!coordinator.IsPrimary)
        {
            var message = shutdownRequested
                ? "shutdown"
                : "show";
            _ = coordinator.NotifyPrimaryAsync(message, cancellation.Token)
                .GetAwaiter()
                .GetResult();
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return SingleInstanceCoordinator.SecondaryInstanceExitCode;
        }

        if (shutdownRequested)
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var context = new TrayApplicationContext(
            languageService,
            runtimeOptions,
            pathResolver,
            languagePreference.UsedInvalidValueFallback);
        coordinator.StartListening(message =>
        {
            context.PostIpcMessage(message);
            return Task.CompletedTask;
        });
        if (TrayLaunchPolicy.ShouldShowMainWindow(args))
        {
            context.PostIpcMessage("show");
        }

        Application.Run(context);
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}
