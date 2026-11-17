using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.EnsureThat;

namespace NzbDrone.Common.Cache
{
    public class Cached<T> : ICached<T>
    {
        private class CacheItem
        {
            public T Object { get; private set; }
            public DateTime? ExpiryTime { get; private set; }

            public CacheItem(T obj, TimeSpan? lifetime = null)
            {
                Object = obj;
                if (lifetime.HasValue)
                {
                    ExpiryTime = DateTime.UtcNow + lifetime.Value;
                }
            }

            public bool IsExpired()
            {
                return ExpiryTime.HasValue && ExpiryTime.Value < DateTime.UtcNow;
            }
        }

        private readonly ConcurrentDictionary<string, CacheItem> _store;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingExpiry = new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly TimeSpan? _defaultLifeTime;
        private readonly bool _rollingExpiry;

        public Cached(TimeSpan? defaultLifeTime = null, bool rollingExpiry = false)
        {
            _store = new ConcurrentDictionary<string, CacheItem>();
            _defaultLifeTime = defaultLifeTime;
            _rollingExpiry = rollingExpiry;
        }

        public void Set(string key, T value, TimeSpan? lifeTime = null)
        {
            Ensure.That(key, () => key).IsNotNullOrWhiteSpace();

            _store[key] = new CacheItem(value, lifeTime ?? _defaultLifeTime);

            if (lifeTime != null)
            {
                ScheduleTryRemove(key, lifeTime.Value);
            }
        }

        public T Find(string key)
        {
            if (!_store.TryGetValue(key, out var cacheItem))
            {
                return default(T);
            }

            if (cacheItem.IsExpired())
            {
                if (TryRemove(key, cacheItem))
                {
                    return default(T);
                }

                if (!_store.TryGetValue(key, out cacheItem))
                {
                    return default(T);
                }
            }

            if (_rollingExpiry && _defaultLifeTime.HasValue)
            {
                _store.TryUpdate(key, new CacheItem(cacheItem.Object, _defaultLifeTime.Value), cacheItem);
                ScheduleTryRemove(key, _defaultLifeTime.Value);
            }

            return cacheItem.Object;
        }

        public void Remove(string key)
        {
            _store.TryRemove(key, out _);
        }

        public int Count => _store.Count;

        public T Get(string key, Func<T> function, TimeSpan? lifeTime = null)
        {
            Ensure.That(key, () => key).IsNotNullOrWhiteSpace();

            lifeTime = lifeTime ?? _defaultLifeTime;

            if (_store.TryGetValue(key, out var cacheItem) && !cacheItem.IsExpired())
            {
                if (_rollingExpiry && lifeTime.HasValue)
                {
                    _store.TryUpdate(key, new CacheItem(cacheItem.Object, lifeTime), cacheItem);
                    ScheduleTryRemove(key, lifeTime.Value);
                }
            }
            else
            {
                var newCacheItem = new CacheItem(function(), lifeTime);
                if (cacheItem != null && _store.TryUpdate(key, newCacheItem, cacheItem))
                {
                    cacheItem = newCacheItem;
                }
                else
                {
                    cacheItem = _store.GetOrAdd(key, newCacheItem);
                }
            }

            return cacheItem.Object;
        }

        public void Clear()
        {
            _store.Clear();
        }

        public void ClearExpired()
        {
            var collection = (ICollection<KeyValuePair<string, CacheItem>>)_store;

            foreach (var cached in _store.Where(c => c.Value.IsExpired()).ToList())
            {
                collection.Remove(cached);
            }
        }

        public ICollection<T> Values
        {
            get
            {
                return _store.Values.Select(c => c.Object).ToList();
            }
        }

        private bool TryRemove(string key, CacheItem value)
        {
            var collection = (ICollection<KeyValuePair<string, CacheItem>>)_store;

            return collection.Remove(new KeyValuePair<string, CacheItem>(key, value));
        }

        private void ScheduleTryRemove(string key, TimeSpan lifeTime)
        {
            // Each Set()/rolling-expiry Find()/Get() call used to schedule a brand new
            // Task.Delay(lifeTime) here without cancelling any delay already pending for
            // the same key. On a long-lived cache (e.g. MediaCoverProxy's 24h cover-url
            // cache) touched more often than its lifetime, orphaned delays never got
            // cancelled and piled up indefinitely - tens of thousands of live
            // TimerQueueTimer/Task chains sitting idle, driving continuous GC/ThreadPool
            // overhead. Track one pending removal per key and cancel the previous one
            // before scheduling a new one so re-touching a key doesn't add another timer.
            var cts = new CancellationTokenSource();

            if (_pendingExpiry.TryRemove(key, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _pendingExpiry[key] = cts;

            Task.Delay(lifeTime, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }

                if (_store.TryGetValue(key, out var cacheItem) && cacheItem.IsExpired())
                {
                    _store.TryRemove(key, out _);
                }

                if (_pendingExpiry.TryGetValue(key, out var current) && current == cts)
                {
                    _pendingExpiry.TryRemove(key, out _);
                    cts.Dispose();
                }
            }, TaskScheduler.Default);
        }
    }
}
