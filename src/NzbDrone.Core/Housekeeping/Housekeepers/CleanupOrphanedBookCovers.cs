using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedBookCovers : IHousekeepingTask, IExecute<CleanupBookCoversCommand>
    {
        private readonly IBookService _bookService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;
        private readonly IConfigService _configService;
        private readonly IMapCoversToLocal _coverService;

        public CleanupOrphanedBookCovers(
            IBookService bookService,
            IDiskProvider diskProvider,
            IAppFolderInfo appFolderInfo,
            Logger logger,
            IConfigService configService,
            IMapCoversToLocal coverService)
        {
            _bookService = bookService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
            _configService = configService;
            _coverService = coverService;
        }

        public void Clean()
        {
            var timer = Stopwatch.StartNew();
            using var coverDirectories = GetBookCoverDirectories().GetEnumerator();

            if (!coverDirectories.MoveNext())
            {
                return;
            }

            var existingBookIds = new HashSet<int>(_bookService.GetBookIds(includeUnmonitored: true));
            var retainedBookIds = _configService.CoverDownloadScope == CoverDownloadScope.LibraryBooks
                ? _bookService.GetLibraryBookIds().ToHashSet()
                : existingBookIds;
            var deletedBookIds = new HashSet<int>();
            var failed = 0;
            var checkedCount = 0;

            do
            {
                var coverDirectory = coverDirectories.Current;
                checkedCount++;
                if (retainedBookIds.Contains(coverDirectory.BookId)) continue;
                // A setting changed during cleanup must not authorize further library-cache removal.
                if (_configService.CoverDownloadScope != CoverDownloadScope.LibraryBooks &&
                    existingBookIds.Contains(coverDirectory.BookId))
                {
                    continue;
                }

                try
                {
                    _coverService.DeleteBookCovers(coverDirectory.BookId);
                    deletedBookIds.Add(coverDirectory.BookId);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.Warn(ex, "Unable to remove cached book-cover directory {0}", coverDirectory.Path);
                }
            }
            while (coverDirectories.MoveNext());

            if (deletedBookIds.Any())
            {
                // Additions, monitoring or imports may have made a deleted cover eligible during the sweep.
                var nowRetainedIds = _configService.CoverDownloadScope == CoverDownloadScope.LibraryBooks
                    ? _bookService.GetLibraryBookIds(deletedBookIds)
                    : deletedBookIds.ToList();
                var nowRetainedBooks = _bookService.GetExistingBooks(nowRetainedIds);
                if (nowRetainedBooks.Any())
                {
                    _coverService.EnsureBookCoversForBooks(nowRetainedBooks);
                }

                _logger.Info("Removed {0} cached book-cover folders; {1} became eligible again; {2} failed; {3} folders checked in {4} ms",
                    deletedBookIds.Count, nowRetainedBooks.Count, failed, checkedCount, timer.ElapsedMilliseconds);
            }
            else
            {
                _logger.Debug("Book-cover cleanup checked {0} folders in {1} ms; {2} failed",
                    checkedCount, timer.ElapsedMilliseconds, failed);
            }
        }

        public void Execute(CleanupBookCoversCommand message)
        {
            Clean();
        }

        public (long Bytes, int BookCount) GetLibraryCleanupEstimate(CancellationToken cancellationToken)
        {
            return GetLibraryCleanupEstimate(cancellationToken, TimeSpan.FromSeconds(3));
        }

        internal (long Bytes, int BookCount) GetLibraryCleanupEstimate(CancellationToken cancellationToken, TimeSpan maximumDuration)
        {
            var timer = Stopwatch.StartNew();

            void CheckBudget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= maximumDuration)
                {
                    _logger.Warn("Book-cover savings estimate exceeded its time budget after {0} ms", timer.ElapsedMilliseconds);
                    throw new TimeoutException("Book-cover savings could not be estimated in time");
                }
            }

            CheckBudget();
            var coverDirectories = GetBookCoverDirectories();
            CheckBudget();
            if (!coverDirectories.Any())
            {
                return (0, 0);
            }

            var libraryBookIds = _bookService.GetLibraryBookIds().ToHashSet();
            var candidates = coverDirectories.Where(item => !libraryBookIds.Contains(item.BookId))
                .OrderBy(item => item.BookId).ToList();
            CheckBudget();

            const int maximumSampleSize = 500;
            var sampleSize = Math.Min(candidates.Count, maximumSampleSize);
            var random = new Random(candidates.Count);
            long sampleBytes = 0;

            for (var index = 0; index < sampleSize; index++)
            {
                CheckBudget();
                if (sampleSize < candidates.Count)
                {
                    // Stable random sampling avoids bias from clustered cover sizes and directory order.
                    var selected = random.Next(index, candidates.Count);
                    (candidates[index], candidates[selected]) = (candidates[selected], candidates[index]);
                }

                try
                {
                    sampleBytes += _diskProvider.GetFolderSize(candidates[index].Path);
                }
                catch (DirectoryNotFoundException)
                {
                    // Cleanup may remove a sampled cache folder after enumeration; it now contributes zero.
                }
            }

            CheckBudget();
            var bytes = sampleSize == 0 ? 0 : (long)Math.Round((double)sampleBytes / sampleSize * candidates.Count);
            _logger.Debug("Estimated {0} removable book-cover bytes from {1} sampled folders out of {2} candidates in {3} ms",
                bytes, sampleSize, candidates.Count, timer.ElapsedMilliseconds);

            return (bytes, candidates.Count);
        }

        private IEnumerable<(string Path, int BookId)> GetBookCoverDirectories()
        {
            var coverRoot = Path.Combine(_appFolderInfo.AppDataFolder, "MediaCover", "Books");
            if (!_diskProvider.FolderExists(coverRoot))
            {
                return Enumerable.Empty<(string Path, int BookId)>();
            }

            return _diskProvider.GetDirectories(coverRoot)
                .Select(path => (Path: path, BookId: ParseBookId(path)))
                .Where(item => item.BookId.HasValue)
                .Select(item => (Path: item.Path, BookId: item.BookId.Value));
        }

        private static int? ParseBookId(string path)
        {
            var directoryName = Path.GetFileName(path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return int.TryParse(directoryName, NumberStyles.None, CultureInfo.InvariantCulture, out var bookId) && bookId > 0 &&
                bookId.ToString(CultureInfo.InvariantCulture) == directoryName
                ? bookId
                : null;
        }
    }
}
