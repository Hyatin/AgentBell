using System.Text;
using System.Text.Json;

namespace AgentBell.Desktop;

/// <summary>Writes sanitized diagnostics with bounded file size and retention.</summary>
public sealed class RollingDesktopDiagnosticLogger : IDesktopDiagnosticLogger
{
    /// <summary>The default maximum size of one diagnostic file.</summary>
    public const long DefaultMaximumFileBytes = 5L * 1024 * 1024;

    /// <summary>The default total number of current and rotated files.</summary>
    public const int DefaultRetainedFileCount = 5;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFileCount;

    /// <summary>Initializes a bounded logger at an explicit path.</summary>
    public RollingDesktopDiagnosticLogger(
        string path,
        long maximumFileBytes = DefaultMaximumFileBytes,
        int retainedFileCount = DefaultRetainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFileCount, 1);
        _path = Path.GetFullPath(path);
        _maximumFileBytes = maximumFileBytes;
        _retainedFileCount = retainedFileCount;
    }

    /// <inheritdoc />
    public void Record(DesktopDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        try
        {
            var json = JsonSerializer.Serialize(diagnosticEvent, SerializerOptions);
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                if (File.Exists(_path)
                    && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(json) + 2
                        > _maximumFileBytes)
                {
                    Rotate();
                }

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
            // Product logging is best-effort and cannot terminate the Tray.
        }
    }

    private void Rotate()
    {
        for (var index = _retainedFileCount - 1; index >= 1; index--)
        {
            var source = index == 1 ? _path : $"{_path}.{index - 1}";
            var destination = $"{_path}.{index}";
            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }
}
