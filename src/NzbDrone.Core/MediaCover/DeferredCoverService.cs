using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Http;

namespace NzbDrone.Core.MediaCover
{
    public interface IDeferredCoverService
    {
        bool MarkBookForCoverDownload(int bookId);
        bool MarkBooksForCoverDownload(IEnumerable<int> bookIds);
        void RemovePendingBooks(IEnumerable<int> bookIds);
        void RequeueBooksForCoverDownload(IEnumerable<int> bookIds, IReadOnlyCollection<string> origins = null, bool attempted = false);
        List<int> TakePendingBookIds();
        bool IsCoverDownloadDeferred { get; set; }
    }

    public class DeferredCoverService : IDeferredCoverService
    {
        private OrderedDictionary<int, (IReadOnlyCollection<string> Origins, bool Attempted)> _pendingBookIds = new();
        private readonly IHttpServiceHealth _imageHostHealth;
        private readonly object _stateLock = new object();
        private readonly Logger _logger;
        private bool _isCoverDownloadDeferred;

        public bool IsCoverDownloadDeferred
        {
            get
            {
                lock (_stateLock)
                {
                    return _isCoverDownloadDeferred;
                }
            }

            set
            {
                lock (_stateLock)
                {
                    _isCoverDownloadDeferred = value;
                }
            }
        }

        public DeferredCoverService(Logger logger, IHttpServiceHealth imageHostHealth)
        {
            _logger = logger;
            _imageHostHealth = imageHostHealth;
        }

        public bool MarkBookForCoverDownload(int bookId)
        {
            var marked = false;
            lock (_stateLock)
            {
                if (!_isCoverDownloadDeferred)
                {
                    return false;
                }

                marked = !_pendingBookIds.ContainsKey(bookId);
                // New work may select a different edition or host; reconsider its covers.
                _pendingBookIds[bookId] = (System.Array.Empty<string>(), false);
            }

            if (marked)
            {
                _logger.Trace("Marked book {0} for deferred cover download", bookId);
            }

            return true;
        }

        public bool MarkBooksForCoverDownload(IEnumerable<int> bookIds)
        {
            var ids = bookIds?.ToList() ?? new List<int>();

            lock (_stateLock)
            {
                if (!_isCoverDownloadDeferred)
                {
                    return false;
                }

                foreach (var bookId in ids)
                {
                    _pendingBookIds[bookId] = (System.Array.Empty<string>(), false);
                }
            }

            if (ids.Any())
            {
                _logger.Debug("Marked {0} books for deferred cover download", ids.Count);
            }

            return true;
        }

        public void RemovePendingBooks(IEnumerable<int> bookIds)
        {
            var ids = bookIds.ToHashSet();
            var removed = 0;
            lock (_stateLock)
            {
                var remaining = new OrderedDictionary<int, (IReadOnlyCollection<string> Origins, bool Attempted)>();
                foreach (var item in _pendingBookIds)
                {
                    if (ids.Contains(item.Key)) removed++;
                    else remaining.Add(item.Key, item.Value);
                }
                _pendingBookIds = remaining;
            }

            if (removed > 0)
            {
                _logger.Debug("Removed {0} books from deferred cover queue", removed);
            }
        }

        public void RequeueBooksForCoverDownload(IEnumerable<int> bookIds, IReadOnlyCollection<string> origins = null, bool attempted = false)
        {
            lock (_stateLock)
            {
                // Latest failure hints supersede older failures, but never a fresh request.
                foreach (var id in bookIds)
                {
                    if (!_pendingBookIds.TryGetValue(id, out var existing) || existing.Origins.Count > 0)
                        _pendingBookIds[id] = (origins ?? System.Array.Empty<string>(), attempted);
                }
            }
        }

        public List<int> TakePendingBookIds()
        {
            lock (_stateLock)
            {
                // Read readiness once per host without reserving a recovery probe. Paused
                // books stay queued without loading their catalog or retrying each URL.
                var ready = _pendingBookIds.Values.SelectMany(value => value.Origins).Distinct()
                    .ToDictionary(origin => origin, origin => _imageHostHealth.CanBeginRequest(origin, out _));
                var unattempted = new List<int>();
                var attempted = new List<int>();
                var remaining = new OrderedDictionary<int, (IReadOnlyCollection<string> Origins, bool Attempted)>();
                var paused = new Dictionary<string, int>();
                foreach (var item in _pendingBookIds)
                {
                    if (item.Value.Origins.Count == 0 || item.Value.Origins.Any(origin => ready[origin]))
                        (item.Value.Attempted ? attempted : unattempted).Add(item.Key);
                    else
                    {
                        remaining.Add(item.Key, item.Value);
                        foreach (var origin in item.Value.Origins)
                            paused[origin] = paused.GetValueOrDefault(origin) + 1;
                    }
                }
                // Rebuild once: removing ordered entries individually shifts the tail each time.
                _pendingBookIds = remaining;
                foreach (var origin in paused)
                    _logger.Debug("Retained {0} pending cover books while {1} is paused", origin.Value, origin.Key);
                unattempted.AddRange(attempted);
                return unattempted;
            }
        }

        internal IEnumerable<int> GetPendingBookIds()
        {
            lock (_stateLock)
            {
                return _pendingBookIds.Keys.ToList();
            }
        }
    }
}
