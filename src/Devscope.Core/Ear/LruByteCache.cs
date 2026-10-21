namespace Devscope.Core.Ear;

/// <summary>
/// Thread-safe least-recently-used cache of byte arrays, bounded by their total size. Concurrent misses on the
/// same key share one factory call, so parallel readers of one nested WAR extract it only once.
/// </summary>
internal sealed class LruByteCache(long capacityBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Slot>> _slots = new(StringComparer.Ordinal);

    // Most recently used first.
    private readonly LinkedList<Slot> _recency = new();
    private long _capacityBytes = capacityBytes;
    private long _sizeBytes;

    /// <summary>Upper bound for the cached bytes; 0 disables caching (values are still returned).</summary>
    public long CapacityBytes
    {
        get
        {
            lock (_gate)
            {
                return _capacityBytes;
            }
        }

        set
        {
            lock (_gate)
            {
                _capacityBytes = Math.Max(0, value);
                Trim();
            }
        }
    }

    /// <summary>Bytes currently held.</summary>
    public long SizeBytes
    {
        get
        {
            lock (_gate)
            {
                return _sizeBytes;
            }
        }
    }

    public byte[] GetOrAdd(string key, Func<byte[]> factory)
    {
        LinkedListNode<Slot> node;
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                node = existing;
            }
            else
            {
                node = _recency.AddFirst(new Slot(key, new Lazy<byte[]>(factory, LazyThreadSafetyMode.ExecutionAndPublication)));
                _slots[key] = node;
            }
        }

        byte[] value;
        try
        {
            value = node.Value.Value.Value;
        }
        catch
        {
            // Lazy caches exceptions; drop the slot so the next caller retries.
            lock (_gate)
            {
                Remove(node);
            }

            throw;
        }

        lock (_gate)
        {
            if (!node.Value.Counted && node.List is not null)
            {
                node.Value.Counted = true;
                node.Value.Size = value.LongLength;
                _sizeBytes += value.LongLength;
                Trim();
            }
        }

        return value;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _slots.Clear();
            _recency.Clear();
            _sizeBytes = 0;
        }
    }

    private void Trim()
    {
        while (_sizeBytes > _capacityBytes && _recency.Last is { } oldest)
        {
            Remove(oldest);
        }
    }

    private void Remove(LinkedListNode<Slot> node)
    {
        if (node.List is null)
        {
            return;
        }

        _recency.Remove(node);
        if (_slots.TryGetValue(node.Value.Key, out var current) && current == node)
        {
            _slots.Remove(node.Value.Key);
        }

        _sizeBytes -= node.Value.Size;
    }

    private sealed class Slot(string key, Lazy<byte[]> value)
    {
        public string Key { get; } = key;

        public Lazy<byte[]> Value { get; } = value;

        public long Size { get; set; }

        public bool Counted { get; set; }
    }
}
