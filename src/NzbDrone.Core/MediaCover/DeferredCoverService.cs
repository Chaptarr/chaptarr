using System.Collections.Generic;
using System.Linq;
using NLog;

namespace NzbDrone.Core.MediaCover
{
    public interface IDeferredCoverService
    {
        bool MarkBookForCoverDownload(int bookId);
        bool MarkBooksForCoverDownload(IEnumerable<int> bookIds);
        void RemovePendingBook(int bookId);
        void RemovePendingBooks(IEnumerable<int> bookIds);
        IEnumerable<int> GetPendingBookIds();
        bool IsCoverDownloadDeferred { get; set; }
    }

    public class DeferredCoverService : IDeferredCoverService
    {
        private readonly HashSet<int> _pendingBookIds = new HashSet<int>();
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

        public DeferredCoverService(Logger logger)
        {
            _logger = logger;
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

                marked = _pendingBookIds.Add(bookId);
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
                    _pendingBookIds.Add(bookId);
                }
            }

            if (ids.Any())
            {
                _logger.Debug("Marked {0} books for deferred cover download", ids.Count);
            }

            return true;
        }

        public void RemovePendingBook(int bookId)
        {
            bool removed;
            lock (_stateLock)
            {
                removed = _pendingBookIds.Remove(bookId);
            }

            if (removed)
            {
                _logger.Trace("Removed book {0} from deferred cover queue", bookId);
            }
        }

        public void RemovePendingBooks(IEnumerable<int> bookIds)
        {
            if (bookIds == null)
            {
                return;
            }

            var removed = 0;

            lock (_stateLock)
            {
                foreach (var bookId in bookIds)
                {
                    if (_pendingBookIds.Remove(bookId))
                    {
                        removed++;
                    }
                }
            }

            if (removed > 0)
            {
                _logger.Debug("Removed {0} books from deferred cover queue", removed);
            }
        }

        public IEnumerable<int> GetPendingBookIds()
        {
            lock (_stateLock)
            {
                return _pendingBookIds.ToList();
            }
        }
    }
}
