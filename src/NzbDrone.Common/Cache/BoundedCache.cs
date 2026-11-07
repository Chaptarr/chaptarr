using System;
using System.Collections.Generic;

namespace NzbDrone.Common.Cache
{
    // Fixed-capacity LRU cache with optional absolute expiration and no background timers.
    public sealed class BoundedCache<T>
    {
        private readonly int _capacity;
        private readonly Func<DateTime> _utcNow;
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries;
        private readonly LinkedList<Entry> _recency = new();
        private readonly object _sync = new();

        private sealed record Entry(string Key, T Value, DateTime? Expires);

        public BoundedCache(int capacity, IEqualityComparer<string> comparer = null, Func<DateTime> utcNow = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _entries = new Dictionary<string, LinkedListNode<Entry>>(comparer ?? StringComparer.Ordinal);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get { lock (_sync) { return _entries.Count; } }
        }

        public bool TryGetValue(string key, out T value)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    if (!node.Value.Expires.HasValue || node.Value.Expires > _utcNow())
                    {
                        _recency.Remove(node);
                        _recency.AddLast(node);
                        value = node.Value.Value;
                        return true;
                    }

                    Remove(node);
                }

                value = default;
                return false;
            }
        }

        public void Set(string key, T value, TimeSpan? lifetime = null)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var previous))
                {
                    Remove(previous);
                }

                if (lifetime.HasValue && lifetime.Value <= TimeSpan.Zero)
                {
                    return;
                }

                while (_entries.Count >= _capacity)
                {
                    Remove(_recency.First);
                }

                var entry = new Entry(key, value, lifetime.HasValue ? _utcNow().Add(lifetime.Value) : null);
                _entries.Add(key, _recency.AddLast(entry));
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _recency.Remove(node);
        }
    }
}
