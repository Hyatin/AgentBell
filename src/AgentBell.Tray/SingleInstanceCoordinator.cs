using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentBell.Desktop;

namespace AgentBell.Tray;

/// <summary>Coordinates one Tray instance per Windows user with bounded current-user IPC.</summary>
public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    /// <summary>Supplies an isolated Mutex/pipe identity only in explicit test mode.</summary>
    public const string TestInstanceIdentityEnvironmentVariable = "AGENTBELL_TEST_INSTANCE_ID";

    /// <summary>The explicit process exit code used by a notified secondary instance.</summary>
    public const int SecondaryInstanceExitCode = 10;

    /// <summary>The maximum UTF-8 IPC payload size.</summary>
    public const int MaximumMessageBytes = 32;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly bool _ownsMutex;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Task? _listenerTask;

    /// <summary>Creates user-scoped Mutex and pipe names without exposing the SID.</summary>
    public SingleInstanceCoordinator(string? testIdentity = null)
    {
        var identity = testIdentity ?? ResolveCurrentOrTestIdentity();
        var suffix = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        _pipeName = $"AgentBell.Tray.{suffix}";
        _mutex = new Mutex(
            initiallyOwned: false,
            $"Local\\AgentBell.Tray.{suffix}",
            out _ownsMutex);
    }

    /// <summary>Gets whether this process owns the user-scoped primary instance.</summary>
    public bool IsPrimary => _ownsMutex;

    /// <summary>Starts the bounded pipe listener for show and shutdown commands.</summary>
    public void StartListening(Func<string, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsPrimary || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = ListenAsync(handler, _listenerCancellation.Token);
    }

    /// <summary>Sends one allowed message to the primary instance.</summary>
    public async Task<bool> NotifyPrimaryAsync(
        string message,
        CancellationToken cancellationToken)
    {
        if (message is not ("show" or "shutdown"))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > MaximumMessageBytes)
        {
            return false;
        }

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _listenerCancellation.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected listener shutdown.
            }
        }

        _mutex.Dispose();
        _listenerCancellation.Dispose();
    }

    private async Task ListenAsync(Func<string, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                MaximumMessageBytes + 1,
                MaximumMessageBytes + 1);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaximumMessageBytes + 1];
            var read = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read is <= 0 or > MaximumMessageBytes)
            {
                continue;
            }

            string message;
            try
            {
                message = new UTF8Encoding(false, true).GetString(buffer, 0, read);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            if (message is "show" or "shutdown")
            {
                await handler(message).ConfigureAwait(false);
            }
        }
    }

    private static string GetCurrentIdentity()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value
                ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static string ResolveCurrentOrTestIdentity()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(DesktopRuntimeOptions.TestModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            return GetCurrentIdentity();
        }

        var value = Environment.GetEnvironmentVariable(TestInstanceIdentityEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("An isolated Tray instance identity is required in test mode.");
        }

        return $"AgentBell.Test.{value}";
    }
}
