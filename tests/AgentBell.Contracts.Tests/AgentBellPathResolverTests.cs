using AgentBell.Contracts;

namespace AgentBell.Contracts.Tests;

public sealed class AgentBellPathResolverTests
{
    [Fact]
    public void Resolver_UsesInjectedKnownFolderForEveryProductPath()
    {
        var knownFolder = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Known-{Guid.NewGuid():N}"));
        var resolver = new AgentBellPathResolver(folder =>
            folder == Environment.SpecialFolder.LocalApplicationData
                ? knownFolder
                : throw new InvalidOperationException("Unexpected folder request."));

        Assert.Equal(knownFolder, resolver.LocalApplicationDataDirectory);
        Assert.Equal(Path.Combine(knownFolder, "AgentBell"), resolver.DataDirectory);
        Assert.Equal(
            Path.Combine(knownFolder, "Programs", "AgentBell"),
            resolver.InstallDirectory);
        Assert.Equal(
            Path.Combine(
                knownFolder,
                "Programs",
                "AgentBell",
                "android",
                "AgentBell-Android-0.7.0-beta.1.apk"),
            resolver.AndroidApkPath);
        Assert.Equal(
            Path.Combine(knownFolder, "Programs", "AgentBell", "AgentBell.Tray.exe"),
            resolver.GetInstalledExecutablePath("AgentBell.Tray.exe"));
    }

    [Fact]
    public void Resolver_DoesNotConsultConflictingLocalAppDataEnvironment()
    {
        using var wrongDirectory = new TemporaryDirectoryPath("AgentBell-Wrong-LocalAppData");
        using var knownDirectory = new TemporaryDirectoryPath("AgentBell-Known-LocalAppData");
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", wrongDirectory.Path);
            var resolver = new AgentBellPathResolver(_ => knownDirectory.Path);

            Assert.StartsWith(
                knownDirectory.Path,
                resolver.DataDirectory,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                wrongDirectory.Path,
                resolver.AndroidApkPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(wrongDirectory.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previous);
        }
    }

    [Fact]
    public void ProductMetadata_IsCentralizedForBeta()
    {
        Assert.Equal("0.7.0", AgentBellProduct.ProductVersion);
        Assert.Equal("0.7.0-beta.1", AgentBellProduct.InformationalVersion);
        Assert.Equal(1, AgentBellProtocol.ProtocolVersion);
    }

    private sealed class TemporaryDirectoryPath : IDisposable
    {
        public TemporaryDirectoryPath(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
