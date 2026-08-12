using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBell.Desktop;

namespace AgentBell.Tray.Tests;

public sealed class RuntimeAndDiagnosticsTests
{
    [Fact]
    public void DesktopDiagnosticFactory_UsesKnownFolderAndLeavesWrongEnvironmentUntouched()
    {
        using var root = new TemporaryDirectory();
        var knownFolder = Path.Combine(root.Path, "known");
        var wrongFolder = Path.Combine(root.Path, "wrong");
        Directory.CreateDirectory(knownFolder);
        Directory.CreateDirectory(wrongFolder);
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var previousEnabled = Environment.GetEnvironmentVariable(
            DesktopDiagnosticLoggerFactory.EnabledEnvironmentVariable);
        var previousPath = Environment.GetEnvironmentVariable(
            DesktopDiagnosticLoggerFactory.PathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", wrongFolder);
            Environment.SetEnvironmentVariable(
                DesktopDiagnosticLoggerFactory.EnabledEnvironmentVariable,
                "1");
            Environment.SetEnvironmentVariable(
                DesktopDiagnosticLoggerFactory.PathEnvironmentVariable,
                null);
            var logger = DesktopDiagnosticLoggerFactory.CreateFromEnvironment(
                new AgentBell.Contracts.AgentBellPathResolver(_ => knownFolder));

            logger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                HttpStatusCode = 202,
                ElapsedMilliseconds = 1,
                PersistenceSucceeded = true,
            });

            Assert.True(File.Exists(Path.Combine(
                knownFolder,
                "AgentBell",
                "logs",
                "desktop.ndjson")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(wrongFolder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
            Environment.SetEnvironmentVariable(
                DesktopDiagnosticLoggerFactory.EnabledEnvironmentVariable,
                previousEnabled);
            Environment.SetEnvironmentVariable(
                DesktopDiagnosticLoggerFactory.PathEnvironmentVariable,
                previousPath);
        }
    }

    [Fact]
    public void RuntimeOptions_TestEnvironmentRequiresExplicitModeAndIsolatedPaths()
    {
        using var directory = new TemporaryDirectory();
        var priorMode = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestModeEnvironmentVariable);
        var priorLoopback = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable);
        var priorLan = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestLanPortEnvironmentVariable);
        var priorData = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.DataHomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                "1");
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable,
                "45100");
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLanPortEnvironmentVariable,
                "45101");
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.DataHomeEnvironmentVariable,
                directory.Path);

            var options = DesktopRuntimeOptions.CreateDefault();

            Assert.True(options.TestIsolationEnabled);
            Assert.Equal(45100, options.LoopbackPort);
            Assert.Equal(45101, options.LanFirstPort);
            Assert.Equal(45101, options.LanLastPort);
            Assert.Equal(IPAddress.Loopback, options.LanAddressOverride);
            Assert.StartsWith(directory.Path, options.EventsFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(directory.Path, options.ConfigFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(directory.Path, options.DiagnosticLogPath, StringComparison.OrdinalIgnoreCase);
            options.Validate();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                priorMode);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable,
                priorLoopback);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLanPortEnvironmentVariable,
                priorLan);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.DataHomeEnvironmentVariable,
                priorData);
        }

        var accidentalOverride = new DesktopRuntimeOptions
        {
            EventsFilePath = Path.Combine(directory.Path, "outside-test-mode.json"),
            LoopbackPort = 45102,
        };
        Assert.Throws<InvalidOperationException>(accidentalOverride.Validate);
    }

    [Fact]
    public void RuntimeOptions_ProductionDataUsesKnownFolderWhenLocalAppDataEnvironmentDiffers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var knownFolderDirectory = new TemporaryDirectory();
        var knownFolder = knownFolderDirectory.Path;
        var expectedDataDirectory = Path.GetFullPath(Path.Combine(knownFolder, "AgentBell"));
        var conflictingEnvironmentPath = Path.Combine(
            Path.GetTempPath(),
            $"AgentBell-Conflicting-LocalAppData-{Guid.NewGuid():N}");
        var priorLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var priorMode = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestModeEnvironmentVariable);
        var priorLoopback = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable);
        var priorLan = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.TestLanPortEnvironmentVariable);
        var priorData = Environment.GetEnvironmentVariable(
            DesktopRuntimeOptions.DataHomeEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(conflictingEnvironmentPath);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", conflictingEnvironmentPath);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLanPortEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.DataHomeEnvironmentVariable,
                null);

            var resolver = new AgentBell.Contracts.AgentBellPathResolver(_ => knownFolder);
            var options = DesktopRuntimeOptions.CreateDefault(resolver);

            Assert.Equal(
                expectedDataDirectory,
                options.DataDirectoryPath,
                StringComparer.OrdinalIgnoreCase);
            Assert.False(options.DataDirectoryPath!.StartsWith(
                conflictingEnvironmentPath,
                StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                Path.Combine(knownFolder, "AgentBell", "logs", "tray.ndjson"),
                options.DiagnosticLogPath);
            Assert.Empty(Directory.EnumerateFileSystemEntries(conflictingEnvironmentPath));
        }
        finally
        {
            if (Directory.Exists(conflictingEnvironmentPath))
            {
                Directory.Delete(conflictingEnvironmentPath, recursive: true);
            }

            Environment.SetEnvironmentVariable("LOCALAPPDATA", priorLocalAppData);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestModeEnvironmentVariable,
                priorMode);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable,
                priorLoopback);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.TestLanPortEnvironmentVariable,
                priorLan);
            Environment.SetEnvironmentVariable(
                DesktopRuntimeOptions.DataHomeEnvironmentVariable,
                priorData);
        }
    }

    [Fact]
    public async Task Runtime_LanUnavailable_KeepsLoopbackRunningAndReleasesPortOnExit()
    {
        using var directory = new TemporaryDirectory();
        var port = GetFreeTcpPort();
        var options = new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            EventsFilePath = Path.Combine(directory.Path, "events.json"),
            ConfigFilePath = null,
            PairingQrCodePath = null,
            LoopbackPort = port,
        };
        await using var runtime = new AgentBellRuntime(options);

        await runtime.StartAsync(CancellationToken.None);
        var snapshot = await runtime.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(RuntimeServiceStatus.Running, snapshot.LocalHookService);
        Assert.Equal(RuntimeServiceStatus.Unavailable, snapshot.LanService);
        Assert.Equal("lan_paths_unavailable", snapshot.LanResultCode);

        await runtime.StopAsync(CancellationToken.None);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task Runtime_LoopbackPortOccupied_ReportsStableHostFailure()
    {
        using var directory = new TemporaryDirectory();
        var port = GetFreeTcpPort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        await using var runtime = new AgentBellRuntime(new DesktopRuntimeOptions
        {
            TestIsolationEnabled = true,
            EventsFilePath = Path.Combine(directory.Path, "events.json"),
            LoopbackPort = port,
        });

        var error = await Assert.ThrowsAsync<AgentBellRuntimeException>(() =>
            runtime.StartAsync(CancellationToken.None));

        Assert.Equal("loopback_start_failed", error.ErrorCode);
    }

    [Fact]
    public async Task Runtime_RestartPreservesPairingConfigurationAndEventsByteForByte()
    {
        using var directory = new TemporaryDirectory();
        var loopbackPort = GetFreeTcpPort();
        var options = DesktopRuntimeOptions.CreateIsolatedTest(
            directory.Path,
            loopbackPort,
            GetFreeTcpPort(loopbackPort));
        var configPath = Assert.IsType<string>(options.ConfigFilePath);
        var protector = new ReversibleTestTokenProtector();

        await using (var firstRuntime = new AgentBellRuntime(options, tokenProtector: protector))
        {
            await firstRuntime.StartAsync(CancellationToken.None);
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{options.LoopbackPort}"),
                Timeout = TimeSpan.FromSeconds(2),
            };
            using var content = new StringContent(
                """
                {"hook_event_name":"Stop","session_id":"upgrade-session","turn_id":"upgrade-turn","cwd":"C:\\Private\\Project","last_assistant_message":"private summary"}
                """,
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync("/api/v1/events/codex", content);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await firstRuntime.StopAsync(CancellationToken.None);
        }

        var configHash = SHA256.HashData(await File.ReadAllBytesAsync(configPath));
        var eventsHash = SHA256.HashData(await File.ReadAllBytesAsync(options.EventsFilePath));
        using var configDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var deviceId = configDocument.RootElement.GetProperty("deviceId").GetString();
        var encryptedToken = configDocument.RootElement.GetProperty("encryptedPairingToken").GetString();

        await using (var upgradedRuntime = new AgentBellRuntime(options, tokenProtector: protector))
        {
            await upgradedRuntime.StartAsync(CancellationToken.None);
            await upgradedRuntime.StopAsync(CancellationToken.None);
        }

        Assert.Equal(configHash, SHA256.HashData(await File.ReadAllBytesAsync(configPath)));
        Assert.Equal(eventsHash, SHA256.HashData(await File.ReadAllBytesAsync(options.EventsFilePath)));
        using var upgradedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        Assert.Equal(deviceId, upgradedDocument.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(
            encryptedToken,
            upgradedDocument.RootElement.GetProperty("encryptedPairingToken").GetString());
    }

    [Fact]
    public async Task Runtime_CorruptConfig_KeepsLocalServiceAvailableAndQuarantinesInput()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "config.json");
        await File.WriteAllTextAsync(configPath, "{invalid", new UTF8Encoding(false));
        var loopbackPort = GetFreeTcpPort();
        var options = DesktopRuntimeOptions.CreateIsolatedTest(
            directory.Path,
            loopbackPort,
            GetFreeTcpPort(loopbackPort)) with
        {
            ConfigFilePath = configPath,
        };
        await using var runtime = new AgentBellRuntime(
            options,
            tokenProtector: new ReversibleTestTokenProtector());

        await runtime.StartAsync(CancellationToken.None);
        var snapshot = await runtime.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(RuntimeServiceStatus.Running, snapshot.LocalHookService);
        Assert.Single(Directory.GetFiles(directory.Path, "config.json.corrupt-*"));
    }

    [Fact]
    public async Task DiagnosticExporter_OmitsEncryptedTokenPathsMessagesAndRawIdentifiers()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "config.json");
        const string Encrypted = "encrypted-secret-value-that-must-not-export";
        await File.WriteAllTextAsync(
            configPath,
            $$"""
              {"protocolVersion":1,"deviceId":"private-device-id","encryptedPairingToken":"{{Encrypted}}"}
              """,
            new UTF8Encoding(false));
        await using var runtime = new AgentBellRuntime(new DesktopRuntimeOptions
        {
            EventsFilePath = Path.Combine(directory.Path, "events.json"),
            ConfigFilePath = configPath,
            PairingQrCodePath = Path.Combine(directory.Path, "pairing", "code.png"),
        });
        var output = Path.Combine(directory.Path, "diagnostics.zip");

        var result = await new DiagnosticExporter().ExportAsync(
            output,
            runtime,
            "Installed",
            1,
            "C:\\Users\\Private\\.codex\\hooks.json",
            CancellationToken.None);

        Assert.True(result.Success);
        using var archive = ZipFile.OpenRead(output);
        var entry = Assert.Single(archive.Entries);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain(Encrypted, text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-device-id", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\Private", text, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", text, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("0.7.0", document.RootElement.GetProperty("productVersion").GetString());
        Assert.Equal(
            "0.7.0-beta.1",
            document.RootElement.GetProperty("informationalVersion").GetString());
    }

    [Fact]
    public void RollingLogger_BoundsFilesAndSerializesOnlyDiagnosticContract()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "tray.ndjson");
        var logger = new RollingDesktopDiagnosticLogger(path, 1024, 3);

        for (var index = 0; index < 50; index++)
        {
            logger.Record(new DesktopDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = "tray",
                HttpStatusCode = 0,
                ElapsedMilliseconds = index,
                PersistenceSucceeded = true,
                Result = "success",
            });
        }

        var files = Directory.GetFiles(directory.Path, "tray.ndjson*");
        Assert.InRange(files.Length, 2, 3);
        var text = string.Join('\n', files.Select(File.ReadAllText));
        Assert.DoesNotContain("summary", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentBell-Tray-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    private static int GetFreeTcpPort(params int[] excludedPorts)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != DesktopHost.ListenPort
                && !LanPortRange.Contains(port)
                && !excludedPorts.Contains(port))
            {
                return port;
            }
        }
    }

    private sealed class ReversibleTestTokenProtector : IPairingTokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
