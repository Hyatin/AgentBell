using System.Text;
using System.Text.Json;

namespace AgentBell.Hook;

/// <summary>Appends sanitized diagnostics as one compact JSON object per line.</summary>
public sealed class JsonDiagnosticLogger : IDiagnosticLogger
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;

    /// <summary>Initializes a diagnostic logger for the given file path.</summary>
    public JsonDiagnosticLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public void Record(HookDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        try
        {
            var directory = Path.GetDirectoryName(_path);
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
        catch
        {
            // Diagnostics are best-effort and must not delay or fail the notify process.
        }
    }
}

