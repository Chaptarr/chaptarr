using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedBookCovers : IHousekeepingTask
    {
        private readonly IBookService _bookService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        public CleanupOrphanedBookCovers(
            IBookService bookService,
            IDiskProvider diskProvider,
            IAppFolderInfo appFolderInfo,
            Logger logger)
        {
            _bookService = bookService;
            _diskProvider = diskProvider;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public void Clean()
        {
            var coverRoot = Path.Combine(_appFolderInfo.AppDataFolder, "MediaCover", "Books");
            if (!_diskProvider.FolderExists(coverRoot))
            {
                return;
            }

            var coverDirectories = _diskProvider.GetDirectories(coverRoot)
                .Select(path => new { Path = path, BookId = ParseBookId(path) })
                .Where(item => item.BookId.HasValue)
                .ToList();

            if (!coverDirectories.Any())
            {
                return;
            }

            var existingBookIds = new HashSet<int>(_bookService.GetBookIds(includeUnmonitored: true));
            var deleted = 0;

            foreach (var coverDirectory in coverDirectories.Where(item => !existingBookIds.Contains(item.BookId.Value)))
            {
                try
                {
                    _diskProvider.DeleteFolder(coverDirectory.Path, recursive: true);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to remove orphaned book-cover directory {0}", coverDirectory.Path);
                }
            }

            if (deleted > 0)
            {
                _logger.Info("Removed {0} orphaned book-cover directories", deleted);
            }
        }

        private static int? ParseBookId(string path)
        {
            var directoryName = Path.GetFileName(path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return int.TryParse(directoryName, NumberStyles.None, CultureInfo.InvariantCulture, out var bookId) && bookId > 0
                ? bookId
                : null;
        }
    }
}
