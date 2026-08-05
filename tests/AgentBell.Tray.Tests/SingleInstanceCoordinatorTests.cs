using AgentBell.Desktop;

namespace AgentBell.Tray.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondInstance_SendsShowToOnlyPrimaryInstance()
    {
        var identity = $"AgentBell-Test-{Guid.NewGuid():N}";
        await using var primary = new SingleInstanceCoordinator(identity);
        await using var secondary = new SingleInstanceCoordinator(identity);
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(message =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await secondary.NotifyPrimaryAsync("show", timeout.Token));

        Assert.Equal("show", await received.Task.WaitAsync(timeout.Token));
        Assert.Equal(10, SingleInstanceCoordinator.SecondaryInstanceExitCode);
    }

    [Fact]
    public async Task Ipc_RejectsUnknownOrOversizedMessages()
    {
        await using var coordinator = new SingleInstanceCoordinator(
            $"AgentBell-Test-{Guid.NewGuid():N}");

        Assert.False(await coordinator.NotifyPrimaryAsync("token=must-not-pass", CancellationToken.None));
        Assert.False(await coordinator.NotifyPrimaryAsync(new string('x', 33), CancellationToken.None));
    }

    [Fact]
    public void LaunchPolicy_ShutdownWithoutPrimaryExitsAndInteractiveInstallShowsWindow()
    {
        Assert.True(TrayLaunchPolicy.IsShutdownRequest(["--shutdown"]));
        Assert.False(TrayLaunchPolicy.ShouldShowMainWindow(["--shutdown"]));
        Assert.False(TrayLaunchPolicy.ShouldShowMainWindow(["--startup"]));
        Assert.True(TrayLaunchPolicy.ShouldShowMainWindow([]));
    }

    [Fact]
    public async Task TestMode_RequiresAndUsesAnIsolatedInstanceIdentity()
    {
        var priorMode = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestModeEnvironmentVariable);
        var priorIdentity = Environment.GetEnvironmentVariable(
            SingleInstanceCoordinator.TestInstanceIdentityEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                "1");
            Environment.SetEnvironmentVariable(
                SingleInstanceCoordinator.TestInstanceIdentityEnvironmentVariable,
                null);
            Assert.Throws<InvalidOperationException>(() => new SingleInstanceCoordinator());

            Environment.SetEnvironmentVariable(
                SingleInstanceCoordinator.TestInstanceIdentityEnvironmentVariable,
                Guid.NewGuid().ToString("N"));
            await using var isolated = new SingleInstanceCoordinator();
            Assert.True(isolated.IsPrimary);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                priorMode);
            Environment.SetEnvironmentVariable(
                SingleInstanceCoordinator.TestInstanceIdentityEnvironmentVariable,
                priorIdentity);
        }
    }
}
