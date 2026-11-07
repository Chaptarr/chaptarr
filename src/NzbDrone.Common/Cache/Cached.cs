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

            lifeTime ??= _defaultLifeTime;
            lock (_pendingExpiry)
            {
                _store[key] = new CacheItem(value, lifeTime);
                CancelExpiry(key);
                if (lifeTime.HasValue)
                {
                    ScheduleTryRemove(key);
                }
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
                ScheduleTryRemove(key);
            }

            return cacheItem.Object;
        }

        public void Remove(string key)
        {
            lock (_pendingExpiry)
            {
                _store.TryRemove(key, out _);
                CancelExpiry(key);
            }
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
                    ScheduleTryRemove(key);
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

                if (cacheItem.ExpiryTime.HasValue)
                {
                    ScheduleTryRemove(key);
                }
            }

            return cacheItem.Object;
        }

        public void Clear()
        {
            lock (_pendingExpiry)
            {
                _store.Clear();
                foreach (var key in _pendingExpiry.Keys)
                {
                    CancelExpiry(key);
                }
            }
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

        // Call while holding _pendingExpiry's lock so cancellation cannot race disposal.
        private void CancelExpiry(string key)
        {
            if (_pendingExpiry.TryRemove(key, out var pending))
            {
                pending.Cancel();
                pending.Dispose();
            }
        }

        private void ScheduleTryRemove(string key)
        {
            lock (_pendingExpiry)
            {
                CancelExpiry(key);
                if (!_store.TryGetValue(key, out var scheduledItem) || !scheduledItem.ExpiryTime.HasValue)
                {
                    return;
                }

                var lifeTime = scheduledItem.ExpiryTime.Value - DateTime.UtcNow;
                var cts = new CancellationTokenSource();
                _pendingExpiry[key] = cts;
                var delay = lifeTime > TimeSpan.Zero ? lifeTime : TimeSpan.Zero;
                Task.Delay(delay, cts.Token).ContinueWith(task =>
                {
                    lock (_pendingExpiry)
                    {
                        if (task.IsCanceled || !_pendingExpiry.TryGetValue(key, out var current) || current != cts)
                        {
                            return;
                        }

                        _pendingExpiry.TryRemove(key, out _);
                        cts.Dispose();
                        if (!_store.TryGetValue(key, out var cacheItem))
                        {
                            return;
                        }

                        if (cacheItem.IsExpired())
                        {
                            // Do not delete a replacement published after the expiry check.
                            TryRemove(key, cacheItem);
                        }
                        else if (cacheItem.ExpiryTime.HasValue)
                        {
                            ScheduleTryRemove(key);
                        }
                    }
                }, TaskScheduler.Default);
            }
        }
    }
}
