using System;
using System.Collections.Concurrent;
using System.Linq;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public sealed class DirectDownloadSourceRuntimeCache
    {
        private const int MaxEntries = 64;
        private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(15);

        private readonly ConcurrentDictionary<string, CacheEntry> _entries = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string sourceUrl, out DirectDownloadSourceFamily family)
        {
            family = default;

            if (!_entries.TryGetValue(sourceUrl, out var entry))
            {
                return false;
            }

            if (DateTime.UtcNow - entry.StoredAtUtc > EntryTtl)
            {
                _entries.TryRemove(sourceUrl, out _);
                return false;
            }

            family = entry.Family;
            return true;
        }

        public void Set(string sourceUrl, DirectDownloadSourceFamily family)
        {
            _entries[sourceUrl] = new CacheEntry(family, DateTime.UtcNow);

            if (_entries.Count <= MaxEntries)
            {
                return;
            }

            var overflow = _entries.Count - MaxEntries;
            foreach (var key in _entries.OrderBy(pair => pair.Value.StoredAtUtc).Take(overflow).Select(pair => pair.Key).ToList())
            {
                _entries.TryRemove(key, out _);
            }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(DirectDownloadSourceFamily family, DateTime storedAtUtc)
            {
                Family = family;
                StoredAtUtc = storedAtUtc;
            }

            public DirectDownloadSourceFamily Family { get; }

            public DateTime StoredAtUtc { get; }
        }
    }
}
