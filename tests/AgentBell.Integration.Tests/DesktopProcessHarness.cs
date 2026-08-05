using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgentBell.Contracts;
using AgentBell.Desktop;
using AgentBell.Hook;

namespace AgentBell.Integration.Tests;

internal sealed class DesktopProcessHarness : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumDiagnosticCharacters = 4 * 1024;
    private const string ReadinessPayload = "{\"hook_event_name\":\"ReadinessProbe\"}";

    private readonly Process _process;
    private readonly PairingToken _runtimeCredential;
    private readonly string _configPath;
    private readonly string _pairingQrCodePath;
    private readonly object _outputGate = new();
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private bool _disposed;
    private bool _hasExited;

    private DesktopProcessHarness(
        Process process,
        PairingToken runtimeCredential,
        string configPath,
        string pairingQrCodePath)
    {
        _process = process;
        _runtimeCredential = runtimeCredential;
        _configPath = configPath;
        _pairingQrCodePath = pairingQrCodePath;
        _process.OutputDataReceived += (_, eventArgs) =>
            AppendOutput(_standardOutput, eventArgs.Data);
        _process.ErrorDataReceived += (_, eventArgs) =>
            AppendOutput(_standardError, eventArgs.Data);
    }

    public static TimeSpan DefaultReadinessTimeout =>
        string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(10);

    public string FileName => _process.StartInfo.FileName;

    public string WorkingDirectory => _process.StartInfo.WorkingDirectory;

    public int ArgumentCount => _process.StartInfo.ArgumentList.Count;

    public bool HasExited => _hasExited || !_disposed && _process.HasExited;

    public static DesktopProcessHarness Start(
        string executablePath,
        string workingDirectory,
        string isolationRoot,
        int loopbackPort,
        int lanPort,
        IReadOnlyList<string>? arguments = null,
        string? diagnosticPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationRoot);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Desktop test executable is missing.", executablePath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(workingDirectory);
        }

        var codexHome = Path.Combine(isolationRoot, "codex-home");
        var dataHome = Path.Combine(isolationRoot, "data-home");
        var configPath = Path.Combine(dataHome, "config.json");
        var pairingQrCodePath = Path.Combine(dataHome, "pairing", "agentbell-pairing.png");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(dataHome);
        var runtimeCredential = CreateRuntimeCredential(configPath, lanPort);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove(DiagnosticLoggerFactory.EnabledEnvironmentVariable);
        startInfo.Environment.Remove(DiagnosticLoggerFactory.PathEnvironmentVariable);
        startInfo.Environment[DesktopRuntimeOptions.TestModeEnvironmentVariable] = "1";
        startInfo.Environment[DesktopRuntimeOptions.TestLoopbackPortEnvironmentVariable] =
            loopbackPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[DesktopRuntimeOptions.TestLanPortEnvironmentVariable] =
            lanPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[DesktopRuntimeOptions.DataHomeEnvironmentVariable] = dataHome;
        startInfo.Environment["CODEX_HOME"] = codexHome;
        if (diagnosticPath is not null)
        {
            startInfo.Environment[DesktopDiagnosticLoggerFactory.EnabledEnvironmentVariable] = "1";
            startInfo.Environment[DesktopDiagnosticLoggerFactory.PathEnvironmentVariable] = diagnosticPath;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var harness = new DesktopProcessHarness(
            process,
            runtimeCredential,
            configPath,
            pairingQrCodePath);
        var started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Desktop process did not start.");
            }

            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return harness;
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();
            harness.ClearSensitiveState();
            throw;
        }
    }

    public async Task WaitUntilReadyAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = RequestTimeout,
            UseProxy = false,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            await ThrowIfExitedAsync(endpoint, stopwatch.Elapsed).ConfigureAwait(false);

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            attemptTimeout.CancelAfter(remaining < RequestTimeout ? remaining : RequestTimeout);
            try
            {
                using var content = new StringContent(
                    ReadinessPayload,
                    Encoding.UTF8,
                    "application/json");
                using var response = await client.PostAsync(
                    endpoint,
                    content,
                    attemptTimeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The bounded poll continues until the overall readiness deadline.
            }
            catch (HttpRequestException)
            {
                // Connection refusal is expected while the listener is starting.
            }

            await ThrowIfExitedAsync(endpoint, stopwatch.Elapsed).ConfigureAwait(false);
            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < PollInterval ? remaining : PollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        throw CreateReadinessException(
            "Desktop did not become ready before the bounded timeout.",
            endpoint,
            stopwatch.Elapsed);
    }

    public async Task WaitUntilLanReadyAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await ThrowIfExitedAsync(endpoint, stopwatch.Elapsed).ConfigureAwait(false);
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                attemptTimeout.CancelAfter(RequestTimeout);
                var status = await SendStatusRequestAsync(
                    endpoint,
                    useRuntimeCredential: true,
                    attemptTimeout.Token).ConfigureAwait(false);
                if (status == HttpStatusCode.OK)
                {
                    return;
                }

                if (status == HttpStatusCode.Forbidden)
                {
                    throw CreateReadinessException(
                        "Desktop rejected its runtime test credential.",
                        endpoint,
                        stopwatch.Elapsed);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The bounded poll continues until the overall readiness deadline.
            }
            catch (HttpRequestException)
            {
                // Connection refusal is expected while the LAN listener is starting.
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < PollInterval ? remaining : PollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        throw CreateReadinessException(
            "Desktop LAN status did not become ready before the bounded timeout.",
            endpoint,
            stopwatch.Elapsed);
    }

    public async Task<HttpStatusCode> SendStatusRequestAsync(
        Uri endpoint,
        bool useRuntimeCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var alternateCredential = useRuntimeCredential ? null : CreateDifferentCredential();
        var credential = useRuntimeCredential ? _runtimeCredential : alternateCredential!;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = RequestTimeout,
            UseProxy = false,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    public bool ContainsRuntimeCredential(string? value) =>
        value?.Contains(_runtimeCredential.Value, StringComparison.Ordinal) == true;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _process.WaitForExit();
            _hasExited = true;
        }
        finally
        {
            _disposed = true;
            _process.Dispose();
            ClearSensitiveState();
        }
    }

    private async Task ThrowIfExitedAsync(Uri endpoint, TimeSpan elapsed)
    {
        if (!_process.HasExited)
        {
            return;
        }

        await _process.WaitForExitAsync().ConfigureAwait(false);
        _process.WaitForExit();
        _hasExited = true;
        throw CreateReadinessException(
            "Desktop exited before the ingestion endpoint became ready.",
            endpoint,
            elapsed);
    }

    private InvalidOperationException CreateReadinessException(
        string reason,
        Uri endpoint,
        TimeSpan elapsed)
    {
        var exitCode = _process.HasExited
            ? _process.ExitCode.ToString(CultureInfo.InvariantCulture)
            : "running";
        return new InvalidOperationException(
            $"{reason} ExitCode={exitCode}; FileName={FileName}; "
            + $"WorkingDirectory={WorkingDirectory}; ArgumentCount={ArgumentCount}; "
            + $"Endpoint={endpoint}; WaitedMs={elapsed.TotalMilliseconds:F0}; "
            + $"Stdout={GetSanitizedOutput(_standardOutput)}; "
            + $"Stderr={GetSanitizedOutput(_standardError)}");
    }

    private void AppendOutput(StringBuilder destination, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputGate)
        {
            if (destination.Length < MaximumDiagnosticCharacters)
            {
                destination.AppendLine(RedactCredentialValues(line));
            }
        }
    }

    private string GetSanitizedOutput(StringBuilder source)
    {
        string value;
        lock (_outputGate)
        {
            value = source.ToString();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value.Length <= MaximumDiagnosticCharacters
            ? value.Trim()
            : value[..MaximumDiagnosticCharacters].Trim() + "<TRUNCATED>";
    }

    private static PairingToken CreateRuntimeCredential(string configPath, int lanPort)
    {
        var plaintext = RandomNumberGenerator.GetBytes(PairingToken.ByteLength);
        byte[]? protectedBytes = null;
        var credential = new PairingToken(plaintext);
        try
        {
            protectedBytes = new WindowsDpapiPairingTokenProtector().Protect(plaintext);
            var now = DateTimeOffset.UtcNow;
            var configuration = new AgentBellConfiguration
            {
                ProtocolVersion = AgentBellProtocol.ProtocolVersion,
                DeviceId = CreateRuntimeDeviceId(),
                DeviceName = "Integration Test Desktop",
                EncryptedPairingToken = Convert.ToBase64String(protectedBytes),
                LastLanPort = lanPort,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var saved = new AgentBellConfigStore(configPath)
                .SaveAsync(configuration, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!saved)
            {
                throw new InvalidOperationException("Could not initialize isolated Desktop configuration.");
            }

            return credential;
        }
        catch
        {
            credential.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private static string CreateRuntimeDeviceId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        try
        {
            return Base64Url.Encode(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private PairingToken CreateDifferentCredential()
    {
        while (true)
        {
            var candidate = PairingToken.Generate();
            if (!_runtimeCredential.Matches(candidate.Value))
            {
                return candidate;
            }

            candidate.Dispose();
        }
    }

    private static string RedactCredentialValues(string value)
    {
        var fragmentPrefix = string.Concat("#", "to", "ken", "=");
        var queryPrefix = string.Concat("access_", "to", "ken", "=");
        return RedactDelimitedValue(
            RedactDelimitedValue(value, fragmentPrefix),
            queryPrefix);
    }

    private static string RedactDelimitedValue(string value, string prefix)
    {
        var searchIndex = 0;
        while (searchIndex < value.Length)
        {
            var prefixIndex = value.IndexOf(prefix, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
            {
                return value;
            }

            var sensitiveStart = prefixIndex + prefix.Length;
            var sensitiveEnd = sensitiveStart;
            while (sensitiveEnd < value.Length
                   && value[sensitiveEnd] != '&'
                   && !char.IsWhiteSpace(value[sensitiveEnd]))
            {
                sensitiveEnd++;
            }

            value = string.Concat(
                value.AsSpan(0, sensitiveStart),
                "<REDACTED>",
                value.AsSpan(sensitiveEnd));
            searchIndex = sensitiveStart + "<REDACTED>".Length;
        }

        return value;
    }

    private void ClearSensitiveState()
    {
        _runtimeCredential.Dispose();
        lock (_outputGate)
        {
            _standardOutput.Clear();
            _standardError.Clear();
        }

        DeleteCredentialArtifact(_configPath);
        DeleteCredentialArtifact(_pairingQrCodePath);
    }

    private static void DeleteCredentialArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup is best-effort and must not mask the test result.
        }
    }
}
