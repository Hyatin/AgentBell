using System.Text.Json;
using AgentBell.Contracts;

namespace AgentBell.Desktop;

/// <summary>Loads and atomically replaces the sanitized recent-event JSON file.</summary>
public interface IEventStore
{
    /// <summary>Loads recent events without throwing private I/O details.</summary>
    Task<EventStoreLoadResult> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Persists a complete recent-event snapshot.</summary>
    Task<bool> SaveAsync(IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken);
}

/// <summary>Describes sanitized event-store startup state.</summary>
/// <param name="Events">Valid restored events.</param>
/// <param name="PersistenceSucceeded">Whether the file was read or recovered successfully.</param>
/// <param name="CorruptFileRecovered">Whether a corrupt file was quarantined and recreated.</param>
public sealed record EventStoreLoadResult(
    IReadOnlyList<AgentEvent> Events,
    bool PersistenceSucceeded,
    bool CorruptFileRecovered);

/// <summary>Production JSON implementation of <see cref="IEventStore"/>.</summary>
public sealed class JsonEventStore : IEventStore
{
    /// <summary>The maximum number of recent events persisted by M1.</summary>
    public const int MaxRecentEvents = 100;

    private const long MaxHistoryFileBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        WriteIndented = true,
    };

    private readonly string _path;

    /// <summary>Initializes the store for an absolute or resolvable local file path.</summary>
    public JsonEventStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Gets the configured event-history path.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public async Task<EventStoreLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new EventStoreLoadResult([], true, false);
        }

        try
        {
            var fileInfo = new FileInfo(_path);
            if (fileInfo.Length > MaxHistoryFileBytes)
            {
                throw new InvalidDataException("History file exceeded its private local limit.");
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var events = await JsonSerializer.DeserializeAsync<List<AgentEvent>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (events is null || events.Any(IsInvalid))
            {
                throw new InvalidDataException("History file did not contain valid events.");
            }

            var recent = events
                .OrderBy(item => item.Sequence)
                .TakeLast(MaxRecentEvents)
                .ToArray();
            return new EventStoreLoadResult(recent, true, false);
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            return await RecoverCorruptFileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return new EventStoreLoadResult([], false, false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_path}.tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(directory);
            var recent = events.TakeLast(MaxRecentEvents).ToArray();

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    recent,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
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
                // A failed cleanup must not interrupt ingestion.
            }
        }
    }

    private static bool IsInvalid(AgentEvent item) =>
        string.IsNullOrWhiteSpace(item.EventId)
        || string.IsNullOrWhiteSpace(item.Agent)
        || string.IsNullOrWhiteSpace(item.Status)
        || string.IsNullOrWhiteSpace(item.Title)
        || item.Sequence <= 0;

    private async Task<EventStoreLoadResult> RecoverCorruptFileAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var corruptPath = $"{_path}.corrupt-{timestamp}";
            File.Move(_path, corruptPath);
            var recreated = await SaveAsync([], cancellationToken).ConfigureAwait(false);
            return new EventStoreLoadResult([], recreated, true);
        }
        catch
        {
            return new EventStoreLoadResult([], false, false);
        }
    }
}
