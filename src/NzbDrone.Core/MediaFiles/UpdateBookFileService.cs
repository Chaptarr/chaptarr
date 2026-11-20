using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpdateBookFileService
    {
        void ChangeFileDateForFile(BookFile bookFile, Author author, Book book);
    }

    public class UpdateBookFileService : IUpdateBookFileService,
                                            IHandle<AuthorScannedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IBookService _bookService;
        private readonly IConfigService _configService;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly Logger _logger;

        public UpdateBookFileService(IDiskProvider diskProvider,
                                      IConfigService configService,
                                      IBookService bookService,
                                      IRootFolderWatchingService rootFolderWatchingService,
                                      Logger logger)
        {
            _diskProvider = diskProvider;
            _configService = configService;
            _bookService = bookService;
            _rootFolderWatchingService = rootFolderWatchingService;
            _logger = logger;
        }

        public void ChangeFileDateForFile(BookFile bookFile, Author author, Book book)
        {
            ChangeFileDate(bookFile, book);
        }

        private bool ChangeFileDate(BookFile bookFile, Book book)
        {
            var bookFilePath = bookFile.Path;

            switch (_configService.FileDate)
            {
                case FileDateType.BookReleaseDate:
                    {
                        if (!book.ReleaseDate.HasValue)
                        {
                            _logger.Debug("Could not create valid date to change file [{0}]", bookFilePath);
                            return false;
                        }

                        var relDate = book.ReleaseDate.Value.Kind == DateTimeKind.Local
                            ? book.ReleaseDate.Value.ToUniversalTime()
                            : DateTime.SpecifyKind(book.ReleaseDate.Value, DateTimeKind.Utc);

                        // Compare UTC instants, including unspecified dates from in-memory imports.
                        var oldDateTime = _diskProvider.FileGetLastWrite(bookFilePath);

                        var clampedToEpoch = false;

                        if (OsInfo.IsNotWindows && relDate < DateTime.UnixEpoch)
                        {
                            relDate = DateTime.UnixEpoch;
                            clampedToEpoch = true;
                        }

                        if (!DateTime.Equals(relDate.WithoutTicks(), oldDateTime.WithoutTicks()))
                        {
                            if (clampedToEpoch)
                            {
                                _logger.Debug("Setting date of file to 1970-01-01 as actual airdate is before that time and will not be set properly");
                            }

                            try
                            {
                                // Announce the write so RootFolderWatchingService doesn't treat it as an
                                // external change and trigger a rescan, which would re-run this date pass.
                                _rootFolderWatchingService.ReportFileSystemChangeBeginning(bookFilePath);
                                // Like Sonarr, preserve subseconds and pass local time to the disk setter.
                                _diskProvider.FileSetLastWriteTime(bookFilePath, relDate.WithTicksFrom(oldDateTime).ToLocalTime());
                                _logger.Debug("Date of file [{0}] changed from '{1}' to '{2}'", bookFilePath, oldDateTime, relDate);

                                return true;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn(ex, "Unable to set date of file [" + bookFilePath + "]");
                            }
                        }

                        return false;
                    }
            }

            return false;
        }

        public void Handle(AuthorScannedEvent message)
        {
            if (_configService.FileDate == FileDateType.None)
            {
                return;
            }

            var books = _bookService.GetAuthorBooksWithFiles(message.Author);

            var bookFiles = new List<BookFile>();
            var updated = new List<BookFile>();

            foreach (var book in books)
            {
                var files = book.BookFiles;
                foreach (var file in files)
                {
                    bookFiles.Add(file);
                    if (ChangeFileDate(file, book))
                    {
                        updated.Add(file);
                    }
                }
            }

            if (updated.Any())
            {
                _logger.ProgressDebug("Changed file date for {0} files of {1} in {2}", updated.Count, bookFiles.Count, message.Author.Name);
            }
            else
            {
                _logger.ProgressDebug("No file dates changed for {0}", message.Author.Name);
            }
        }
    }
}
