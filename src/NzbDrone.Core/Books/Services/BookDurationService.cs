using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Books.Services
{
    public interface IBookDurationService
    {
        void UpdateBookDuration(int bookId);
        void UpdateBookDuration(Book book, List<BookFile> bookFiles = null);
        void UpdateAllBookDurations();
    }

    public class BookDurationService : IBookDurationService
    {
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly Logger _logger;

        public BookDurationService(IBookService bookService,
                                  IMediaFileService mediaFileService,
                                  IMediaInfoExtractor mediaInfoExtractor,
                                  Logger logger)
        {
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _mediaInfoExtractor = mediaInfoExtractor;
            _logger = logger;
        }

        public void UpdateBookDuration(int bookId)
        {
            var book = _bookService.GetBook(bookId);
            if (book != null)
            {
                UpdateBookDuration(book);
            }
        }

        public void UpdateBookDuration(Book book, List<BookFile> bookFiles = null)
        {
            if (book == null)
            {
                return;
            }

            if (book.MediaType == BookMediaType.Ebook)
            {
                UpdateBookDurationFromFullFileSet(book, null);
                return;
            }

            // Public callers may pass only the newly touched files, which is not enough to calculate
            // a total book duration. Always fetch the complete tracked set here; bulk repair uses the
            // private trusted-full-set helper below.
            var fullBookFiles = _mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>();
            UpdateBookDurationFromFullFileSet(book, fullBookFiles);
        }

        private void UpdateBookDurationFromFullFileSet(Book book, List<BookFile> bookFiles)
        {
            if (book == null)
            {
                return;
            }

            // DurationMinutes is audiobook-only. Avoid expensive ffprobe/TagLib work for ebooks and ensure stale
            // values don't linger due to previous incorrect calculations.
            if (book.MediaType == BookMediaType.Ebook)
            {
                if (book.DurationMinutes != null)
                {
                    book.DurationMinutes = null;
                    _bookService.UpdateBook(book);
                }

                return;
            }

            bookFiles ??= new List<BookFile>();

            // Only audio files contribute to duration.
            bookFiles = bookFiles
                .Where(f => f != null &&
                            !string.IsNullOrWhiteSpace(f.Path) &&
                            MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(f.Path)))
                .ToList();

            long totalDurationSeconds = 0;
            var fileCount = bookFiles.Count;
            var filesWithDuration = 0;
            var durationUpdated = new List<BookFile>();

            foreach (var file in bookFiles)
            {
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                var durationSeconds = MediaDuration.GetStoredDurationSeconds(file);
                if (!durationSeconds.HasValue)
                {
                    // Backfill only genuinely missing legacy rows. Normal import/scan paths persist DurationSeconds.
                    var mediaInfo = file.MediaInfo ?? new MediaInfoModel();
                    mediaInfo = _mediaInfoExtractor.ExtractMediaInfo(file.Path) ?? mediaInfo;
                    file.MediaInfo = mediaInfo;

                    if (mediaInfo.Duration > TimeSpan.Zero)
                    {
                        durationSeconds = MediaDuration.FromTimeSpan(mediaInfo.Duration);
                        file.DurationSeconds = durationSeconds;
                        durationUpdated.Add(file);
                    }
                }

                if (MediaDuration.HasDuration(durationSeconds))
                {
                    totalDurationSeconds += durationSeconds.Value;
                    filesWithDuration++;
                }
            }

            if (durationUpdated.Count > 0)
            {
                _mediaFileService.Update(durationUpdated);
            }

            int? durationMinutes = null;
            if (fileCount == 0)
            {
                // Case 1: No files exist -> clear duration
                durationMinutes = null;
            }
            else if (filesWithDuration == fileCount && totalDurationSeconds > 0)
            {
                // Case 3: All files have duration -> update
                durationMinutes = (int)Math.Round(TimeSpan.FromSeconds(totalDurationSeconds).TotalMinutes);
            }
            else
            {
                // Case 2: Files exist but we can't reliably calculate duration -> preserve existing
                _logger.Debug("Files exist for book {0} ({1}) but duration could not be calculated (durations present: {2}/{3}); preserving existing duration: {4}",
                    book.Title,
                    book.Id,
                    filesWithDuration,
                    fileCount,
                    book.DurationMinutes?.ToString() ?? "null");
                return;
            }

            if (book.DurationMinutes != durationMinutes)
            {
                book.DurationMinutes = durationMinutes;
                _bookService.UpdateBook(book);

                _logger.Debug("Updated duration for book {0} ({1}): {2} files, {3} minutes",
                    book.Title,
                    book.Id,
                    fileCount,
                    durationMinutes?.ToString() ?? "null");
            }
        }

        public void UpdateAllBookDurations()
        {
            _logger.Debug("Starting explicit duration repair for all books");

            var allBooks = _bookService.GetAllBooks();
            var bookIds = allBooks
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .Distinct()
                .ToList();
            var filesByBookId = (_mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>())
                .Where(file => file?.Edition?.BookId > 0)
                .GroupBy(file => file.Edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var updatedCount = 0;

            foreach (var book in allBooks)
            {
                try
                {
                    var oldDuration = book.DurationMinutes;
                    var bookFiles = book != null && filesByBookId.TryGetValue(book.Id, out var files)
                        ? files
                        : new List<BookFile>();
                    UpdateBookDurationFromFullFileSet(book, bookFiles);

                    if (oldDuration != book.DurationMinutes)
                    {
                        updatedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to update duration for book {0} ({1})", book.Title, book.Id);
                }
            }

            _logger.Debug("Completed explicit duration repair. Updated {0} out of {1} books", updatedCount, allBooks.Count);
        }
    }
}
