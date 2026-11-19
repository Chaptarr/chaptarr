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
        void RequeueBooksForCoverDownload(IEnumerable<int> bookIds);
        List<int> TakePendingBookIds();
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

        public void RequeueBooksForCoverDownload(IEnumerable<int> bookIds)
        {
            lock (_stateLock)
            {
                _pendingBookIds.UnionWith(bookIds);
            }
        }

        public List<int> TakePendingBookIds()
        {
            lock (_stateLock)
            {
                var bookIds = _pendingBookIds.ToList();
                _pendingBookIds.Clear();
                return bookIds;
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
