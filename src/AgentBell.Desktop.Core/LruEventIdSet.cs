namespace AgentBell.Desktop;

/// <summary>Provides a bounded, thread-safe least-recently-used set of event identifiers.</summary>
public sealed class LruEventIdSet
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();

    /// <summary>Initializes the set with the specified positive capacity.</summary>
    public LruEventIdSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Gets the current number of retained identifiers.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Count;
            }
        }
    }

    /// <summary>Adds a new identifier or refreshes an existing one.</summary>
    /// <returns><see langword="true"/> only when the identifier was newly added.</returns>
    public bool TryAdd(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        lock (_gate)
        {
            if (_nodes.TryGetValue(eventId, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddLast(existing);
                return false;
            }

            var node = _recency.AddLast(eventId);
            _nodes.Add(eventId, node);

            if (_nodes.Count > _capacity)
            {
                var leastRecent = _recency.First;
                if (leastRecent is not null)
                {
                    _recency.RemoveFirst();
                    _nodes.Remove(leastRecent.Value);
                }
            }

            return true;
        }
    }
}
