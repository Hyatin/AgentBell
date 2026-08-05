using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Exports a deliberately minimal diagnostic ZIP and rejects sensitive content.</summary>
public sealed class DiagnosticExporter
{
    private static readonly string[] ForbiddenMarkers =
    [
        "authorization",
        "access_token",
        "#token=",
        "?token=",
        "encryptedPairingToken\" : \"",
        "last_assistant_message",
        "input-messages",
        "session_id",
        "turn_id",
        "\"summary\"",
        "\"cwd\"",
    ];

    /// <summary>Creates a sanitized ZIP or deletes the temporary ZIP when scanning fails.</summary>
    public async Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        AgentBellRuntime runtime,
        string integrationState,
        int agentBellHookCount,
        string? hooksPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(runtime);
        var destination = Path.GetFullPath(destinationPath);
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            var snapshot = await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var config = await ReadConfigSummaryAsync(
                runtime.RuntimeOptions.ConfigFilePath,
                cancellationToken).ConfigureAwait(false);
            var manifest = new
            {
                productVersion = AgentBellProduct.ProductVersion,
                informationalVersion = AgentBellProduct.InformationalVersion,
                protocolVersion = AgentBellProtocol.ProtocolVersion,
                generatedAt = DateTimeOffset.UtcNow,
                windowsVersion = Environment.OSVersion.VersionString,
                framework = RuntimeInformation.FrameworkDescription,
                integrationState,
                agentBellHookCount,
                hookPathHash = HashPath(hooksPath),
                localService = snapshot.LocalHookService.ToString(),
                localResult = snapshot.LocalResultCode,
                lanService = snapshot.LanService.ToString(),
                lanResult = snapshot.LanResultCode,
                lanAddressType = ClassifyPrivateAddress(snapshot.LanAddress),
                lanPort = snapshot.LanPort,
                webSocketClients = snapshot.WebSocketClientCount,
                latestSequence = snapshot.LatestSequence,
                eventCount = snapshot.EventCount,
                configuration = config.Summary,
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            var secrets = new[]
                {
                    runtime.GetSensitivePairingTokenForScan(),
                    config.EncryptedPairingToken,
                }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 6)
                .Cast<string>()
                .ToArray();
            if (!IsSanitized(manifestBytes, secrets))
            {
                return DiagnosticExportResult.Failure("sensitive_content_detected");
            }

            var directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return DiagnosticExportResult.Failure("diagnostic_path_invalid");
            }

            Directory.CreateDirectory(directory);
            await using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            if (!await ScanArchiveAsync(temporary, secrets, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(temporary);
                return DiagnosticExportResult.Failure("sensitive_content_detected");
            }

            File.Move(temporary, destination, overwrite: true);
            return DiagnosticExportResult.Available(destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DiagnosticExportResult.Failure("diagnostic_export_failed");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // A failed temporary cleanup cannot reveal content through the result.
            }
        }
    }

    private static async Task<ConfigScanState> ReadConfigSummaryAsync(
        string? configPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new ConfigScanState(
                new { exists = false, valid = true, hasDeviceId = false, hasEncryptedToken = false },
                null);
        }

        try
        {
            await using var stream = File.OpenRead(configPath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var hasDevice = root.TryGetProperty("deviceId", out var device)
                && device.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(device.GetString());
            var encrypted = root.TryGetProperty("encryptedPairingToken", out var token)
                && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
            return new ConfigScanState(
                new
                {
                    exists = true,
                    valid = true,
                    hasDeviceId = hasDevice,
                    hasEncryptedToken = !string.IsNullOrWhiteSpace(encrypted),
                },
                encrypted);
        }
        catch
        {
            return new ConfigScanState(
                new { exists = true, valid = false, hasDeviceId = false, hasEncryptedToken = false },
                null);
        }
    }

    private static async Task<bool> ScanArchiveAsync(
        string path,
        IReadOnlyList<string> secrets,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 1024 * 1024)
            {
                return false;
            }

            await using var entryStream = entry.Open();
            using var memory = new MemoryStream();
            await entryStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (!IsSanitized(memory.ToArray(), secrets))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSanitized(byte[] bytes, IReadOnlyList<string> secrets)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return !ForbiddenMarkers.Any(marker =>
                text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            && !secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));
    }

    private static string? HashPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant()[..12];
    }

    private static string ClassifyPrivateAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || !LanAddressResolver.IsPrivateIpv4(address))
        {
            return "unavailable";
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => "rfc1918-10",
            172 => "rfc1918-172",
            192 => "rfc1918-192",
            _ => "private",
        };
    }

    private sealed record ConfigScanState(object Summary, string? EncryptedPairingToken);
}

/// <summary>Contains the diagnostic export outcome without sensitive values.</summary>
public sealed record DiagnosticExportResult(bool Success, string Code, string? Path)
{
    internal static DiagnosticExportResult Available(string path) =>
        new(true, "success", path);

    internal static DiagnosticExportResult Failure(string code) =>
        new(false, code, null);
}
