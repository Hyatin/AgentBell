namespace AgentBell.Tray.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void KnownFolderResolver_ProducesStableStartupAndApkPaths()
    {
        var wrongEnvironment = Path.Combine(Path.GetTempPath(), $"wrong-{Guid.NewGuid():N}");
        var knownFolder = Path.Combine(Path.GetTempPath(), $"known-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", wrongEnvironment);
            var paths = new AgentBell.Contracts.AgentBellPathResolver(_ => knownFolder);
            var startup = new StartupRegistration(
                paths.GetInstalledExecutablePath("AgentBell.Tray.exe"),
                new MemoryStartupStore());

            Assert.Equal(
                $"\"{Path.Combine(knownFolder, "Programs", "AgentBell", "AgentBell.Tray.exe")}\" --startup",
                startup.ExpectedCommand);
            Assert.Equal(
                Path.Combine(
                    knownFolder,
                    "Programs",
                    "AgentBell",
                    "android",
                    "AgentBell-Android-0.6.0-beta.1.apk"),
                paths.AndroidApkPath);
            Assert.DoesNotContain(wrongEnvironment, startup.ExpectedCommand, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previous);
        }
    }

    [Fact]
    public void EnableDisableAndStatus_AreIdempotentAndQuoteSpaces()
    {
        var store = new MemoryStartupStore();
        var registration = new StartupRegistration(
            "C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Tray.exe",
            store);

        var first = registration.Enable();
        var second = registration.Enable();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(
            "\"C:\\Users\\First Last\\本地程序\\AgentBell\\AgentBell.Tray.exe\" --startup",
            store.Value);
        Assert.Equal(StartupRegistrationState.Enabled, registration.Status().State);

        Assert.True(registration.Disable().Success);
        Assert.True(registration.Disable().Success);
        Assert.Equal(StartupRegistrationState.Disabled, registration.Status().State);
    }

    [Fact]
    public void RegistryFailures_ReturnStableErrors()
    {
        var registration = new StartupRegistration(
            "C:\\Programs\\AgentBell\\AgentBell.Tray.exe",
            new ThrowingStartupStore());

        Assert.Equal("startup_write_failed", registration.Enable().Code);
        Assert.Equal("startup_delete_failed", registration.Disable().Code);
        Assert.Equal("startup_read_failed", registration.Status().Code);
    }

    private sealed class MemoryStartupStore : IStartupValueStore
    {
        public string? Value { get; private set; }

        public int WriteCount { get; private set; }

        public string? Read() => Value;

        public void Write(string value)
        {
            Value = value;
            WriteCount++;
        }

        public void Delete() => Value = null;
    }

    private sealed class ThrowingStartupStore : IStartupValueStore
    {
        public string? Read() => throw new UnauthorizedAccessException();

        public void Write(string value) => throw new UnauthorizedAccessException();

        public void Delete() => throw new UnauthorizedAccessException();
    }
}
