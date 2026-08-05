using System.Text.Json;

namespace AgentBell.Desktop;

/// <summary>Loads and atomically replaces the UTF-8 M2 configuration file.</summary>
public sealed class AgentBellConfigStore
{
    private const long MaxConfigBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        WriteIndented = true,
    };

    private readonly string _path;

    /// <summary>Initializes a store for the specified local configuration path.</summary>
    public AgentBellConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Gets the resolved configuration path.</summary>
    public string Path => _path;

    /// <summary>Loads configuration while quarantining malformed content.</summary>
    public async Task<AgentBellConfigLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new AgentBellConfigLoadResult(null, true, false);
        }

        try
        {
            if (new FileInfo(_path).Length > MaxConfigBytes)
            {
                throw new InvalidDataException("Configuration exceeded its local size limit.");
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var configuration = await JsonSerializer.DeserializeAsync<AgentBellConfiguration>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (configuration is null)
            {
                throw new InvalidDataException("Configuration root was empty.");
            }

            return new AgentBellConfigLoadResult(configuration, true, false);
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            return RecoverCorruptFile();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return new AgentBellConfigLoadResult(null, false, false);
        }
    }

    /// <summary>Saves UTF-8 without BOM using a flushed temporary file and atomic replacement.</summary>
    public async Task<bool> SaveAsync(
        AgentBellConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_path}.tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    configuration,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or JsonException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Temporary-file cleanup cannot affect M1 availability.
            }
        }
    }

    private AgentBellConfigLoadResult RecoverCorruptFile()
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            File.Move(_path, $"{_path}.corrupt-{timestamp}");
            return new AgentBellConfigLoadResult(null, true, true);
        }
        catch
        {
            return new AgentBellConfigLoadResult(null, false, false);
        }
    }
}

/// <summary>Describes safe configuration-load state.</summary>
public sealed record AgentBellConfigLoadResult(
    AgentBellConfiguration? Configuration,
    bool PersistenceSucceeded,
    bool CorruptFileRecovered);
