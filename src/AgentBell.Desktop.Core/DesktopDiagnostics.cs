using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Records optional content-free Desktop diagnostics.</summary>
public interface IDesktopDiagnosticLogger
{
    /// <summary>Records one sanitized request result without exception details.</summary>
    void Record(DesktopDiagnosticEvent diagnosticEvent);
}

/// <summary>Contains only fields permitted in an opt-in Desktop diagnostic record.</summary>
public sealed record DesktopDiagnosticEvent
{
    /// <summary>Gets the diagnostic timestamp.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the local normalized event type, when known.</summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; init; }

    /// <summary>Gets a truncated session hash, when available.</summary>
    [JsonPropertyName("threadIdHash")]
    public string? ThreadIdHash { get; init; }

    /// <summary>Gets a truncated turn hash, when available.</summary>
    [JsonPropertyName("turnIdHash")]
    public string? TurnIdHash { get; init; }

    /// <summary>Gets whether the event was a duplicate.</summary>
    [JsonPropertyName("duplicate")]
    public bool IsDuplicate { get; init; }

    /// <summary>Gets the returned HTTP status code.</summary>
    [JsonPropertyName("httpStatus")]
    public required int HttpStatusCode { get; init; }

    /// <summary>Gets the request processing duration in milliseconds.</summary>
    [JsonPropertyName("elapsedMs")]
    public required long ElapsedMilliseconds { get; init; }

    /// <summary>Gets whether persistence succeeded or was unnecessary.</summary>
    [JsonPropertyName("persistenceSucceeded")]
    public bool PersistenceSucceeded { get; init; }

    /// <summary>Gets the current recent-event count.</summary>
    [JsonPropertyName("eventCount")]
    public int EventCount { get; init; }

    /// <summary>Gets a random short connection identifier, never a client address.</summary>
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }

    /// <summary>Gets whether a LAN request authenticated successfully.</summary>
    [JsonPropertyName("authenticated")]
    public bool? Authenticated { get; init; }

    /// <summary>Gets the protocol message type without its body.</summary>
    [JsonPropertyName("messageType")]
    public string? MessageType { get; init; }

    /// <summary>Gets a sanitized event or resume sequence.</summary>
    [JsonPropertyName("sequence")]
    public long? Sequence { get; init; }

    /// <summary>Gets the bounded outbound queue depth when known.</summary>
    [JsonPropertyName("queueDepth")]
    public int? QueueDepth { get; init; }

    /// <summary>Gets a stable M2 result code.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Gets the number of active authenticated sockets.</summary>
    [JsonPropertyName("activeConnections")]
    public int? ActiveConnections { get; init; }

    /// <summary>Gets the number of replayed events without message content.</summary>
    [JsonPropertyName("replayCount")]
    public int? ReplayCount { get; init; }
}

/// <summary>Creates the default-disabled Desktop diagnostic logger.</summary>
public static class DesktopDiagnosticLoggerFactory
{
    /// <summary>Environment variable that enables Desktop diagnostic NDJSON.</summary>
    public const string EnabledEnvironmentVariable = "AGENTBELL_DESKTOP_DIAGNOSTICS";

    /// <summary>Environment variable that overrides the Desktop diagnostic path.</summary>
    public const string PathEnvironmentVariable = "AGENTBELL_DESKTOP_DIAGNOSTICS_PATH";

    /// <summary>Creates an environment-configured logger without throwing configuration details.</summary>
    public static IDesktopDiagnosticLogger CreateFromEnvironment(
        AgentBellPathResolver? pathResolver = null)
    {
        if (!IsEnabled(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)))
        {
            return NullDesktopDiagnosticLogger.Instance;
        }

        try
        {
            var configuredPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return new JsonDesktopDiagnosticLogger(configuredPath);
            }

            var dataDirectory = (pathResolver ?? new AgentBellPathResolver()).DataDirectory;
            return new JsonDesktopDiagnosticLogger(
                System.IO.Path.Combine(dataDirectory, "logs", "desktop.ndjson"));
        }
        catch
        {
            return NullDesktopDiagnosticLogger.Instance;
        }
    }

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Appends sanitized Desktop diagnostics as UTF-8 NDJSON.</summary>
public sealed class JsonDesktopDiagnosticLogger : IDesktopDiagnosticLogger
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly string _path;

    /// <summary>Initializes a logger at the specified local path.</summary>
    public JsonDesktopDiagnosticLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public void Record(DesktopDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(diagnosticEvent, SerializerOptions);
                using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(json);
            }
        }
        catch
        {
            // Diagnostics are best-effort and never affect event acceptance.
        }
    }
}

/// <summary>Discards diagnostics unless explicitly enabled.</summary>
public sealed class NullDesktopDiagnosticLogger : IDesktopDiagnosticLogger
{
    private NullDesktopDiagnosticLogger()
    {
    }

    /// <summary>Gets the stateless singleton instance.</summary>
    public static NullDesktopDiagnosticLogger Instance { get; } = new();

    /// <inheritdoc />
    public void Record(DesktopDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
    }
}
