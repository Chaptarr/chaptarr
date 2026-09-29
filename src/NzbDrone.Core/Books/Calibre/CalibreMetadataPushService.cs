using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authors;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreMetadataPushService : IExecute<PushCalibreMetadataCommand>, IHandle<BookFileAddedEvent>, IHandle<MediaCoversUpdatedEvent>
    {
        public static readonly string[] IdentityFields = { "title", "authors" };
        public static readonly string[] AllFields =
        {
            "cover", "title", "authors", "series", "comments", "publisher",
            "pubdate", "languages", "tags", "rating", "identifiers"
        };

        private static readonly ConcurrentDictionary<int, DateTime> RecentImportPushes = new ConcurrentDictionary<int, DateTime>();
        private static readonly TimeSpan ImportCoverWindow = TimeSpan.FromMinutes(15);

        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public CalibreMetadataPushService(IBookService bookService,
                                          IAuthorService authorService,
                                          IEditionService editionService,
                                          IMediaFileService mediaFileService,
                                          IRootFolderService rootFolderService,
                                          ICalibreProxy calibreProxy,
                                          IMapCoversToLocal mediaCoverService,
                                          IEventAggregator eventAggregator,
                                          Logger logger)
        {
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _calibreProxy = calibreProxy;
            _mediaCoverService = mediaCoverService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Execute(PushCalibreMetadataCommand message)
        {
            var bookIds = (message.BookIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();

            if (!bookIds.Any())
            {
                return;
            }

            var fields = (message.Fields != null && message.Fields.Any()) ? (ICollection<string>)message.Fields : AllFields;
            var pushed = 0;

            foreach (var bookId in bookIds)
            {
                try
                {
                    var book = PushBook(bookId, fields);

                    if (book != null)
                    {
                        pushed++;
                        _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(book));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to push metadata to calibre for book {0}", bookId);
                }
            }

            _logger.Info("Pushed metadata to calibre for {0} of {1} book(s)", pushed, bookIds.Count);
        }

        public void Handle(BookFileAddedEvent message)
        {
            var bookFile = message.BookFile;
            var book = bookFile?.Edition?.Book;

            if (book == null && bookFile != null && bookFile.EditionId > 0)
            {
                book = _editionService.GetEdition(bookFile.EditionId)?.Book;
            }

            if (book == null || book.Id <= 0)
            {
                return;
            }

            var author = _authorService.GetAuthor(book.AuthorId);

            if (author == null || author.Path.IsNullOrWhiteSpace())
            {
                return;
            }

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(author.Path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", author.Path);
                return;
            }

            if (rootFolder == null || !rootFolder.IsCalibreLibrary || rootFolder.CalibreSettings == null)
            {
                return;
            }

            try
            {
                RecentImportPushes[book.Id] = DateTime.UtcNow;
                PushBook(book.Id, rootFolder.AutoPushCalibreMetadata ? AllFields : IdentityFields);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to push metadata to calibre for {0}", book.Title);
            }
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            var book = message.Book;

            if (book == null || book.Id <= 0)
            {
                return;
            }

            var author = _authorService.GetAuthor(book.AuthorId);

            if (author == null || author.Path.IsNullOrWhiteSpace())
            {
                return;
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(author.Path);

            if (rootFolder?.IsCalibreLibrary != true || !rootFolder.AutoPushCalibreMetadata)
            {
                return;
            }

            foreach (var stale in RecentImportPushes.Where(p => DateTime.UtcNow - p.Value > ImportCoverWindow).Select(p => p.Key).ToList())
            {
                RecentImportPushes.TryRemove(stale, out _);
            }

            if (!RecentImportPushes.TryRemove(book.Id, out _))
            {
                return;
            }

            try
            {
                PushBook(book.Id, new[] { "cover" });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to push the updated cover for {0} to calibre", book.Title);
            }
        }

        private Book PushBook(int bookId, ICollection<string> fields)
        {
            var book = _bookService.GetBook(bookId);

            if (book == null)
            {
                return null;
            }

            var author = _authorService.GetAuthor(book.AuthorId);
            var authorName = author?.Name;

            if (author == null || author.Path.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (fields.Contains("cover", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // EnsureBookCovers refuses to reconcile a cover without hydrated editions.
                    book.Editions = _editionService.GetEditionsByBook(book.Id);
                    _mediaCoverService.EnsureBookCovers(book);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to reconcile the cover for {0} before pushing", book.Title);
                }
            }

            if (!TryGetCalibreSettings(author.Path, out var settings))
            {
                _logger.Debug("{0} is not in a calibre root folder; skipping", book.Title);
                return null;
            }

            var files = _mediaFileService.GetFilesByBook(bookId)
                .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                .ToList();

            if (!files.Any())
            {
                _logger.Debug("No files on disk for {0}; nothing to push", book.Title);
                return null;
            }

            var title = ResolveTitle(files, book);
            var seriesLink = CalibreSeriesSelector.Select(book);
            var series = seriesLink?.Series?.Value?.Title;
            double? seriesIndex = null;

            if (double.TryParse(seriesLink?.Position, out var parsedIndex))
            {
                seriesIndex = parsedIndex;
            }

            if (title.IsNullOrWhiteSpace() && authorName.IsNullOrWhiteSpace() && series.IsNullOrWhiteSpace())
            {
                return null;
            }

            var calibreIds = new HashSet<int>();

            foreach (var file in files)
            {
                var calibreId = file.CalibreId;

                if (calibreId == 0)
                {
                    try
                    {
                        calibreId = _calibreProxy.GetCalibreIdForPath(file.Path, settings);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to resolve calibre id for {0}", file.Path);
                        continue;
                    }

                    if (calibreId == 0)
                    {
                        continue;
                    }

                    file.CalibreId = calibreId;

                    try
                    {
                        _mediaFileService.Update(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to store calibre id for {0}", file.Path);
                    }
                }

                calibreIds.Add(calibreId);
            }

            if (!calibreIds.Any())
            {
                _logger.Debug("No calibre record resolved for {0}; skipping", book.Title);
                return null;
            }

            foreach (var calibreId in calibreIds)
            {
                var reference = files.First(f => f.CalibreId == calibreId);
                var written = _calibreProxy.SetSelectedFields(reference, fields, settings);

                if (written.Any())
                {
                    _logger.Info("Pushed {0} for '{1}' to calibre record {2}", string.Join(", ", written), title, calibreId);
                }

                var skipped = fields.Where(f => !written.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

                if (skipped.Any())
                {
                    _logger.Debug("Nothing to write for {0} on '{1}'", string.Join(", ", skipped), title);
                }
            }

            if (fields.Any(field => IdentityFields.Contains(field, StringComparer.OrdinalIgnoreCase)))
            {
                FollowCalibreRefile(files, settings);
            }

            return book;
        }

        private void FollowCalibreRefile(List<BookFile> files, CalibreSettings settings)
        {
            foreach (var file in files.Where(f => f.CalibreId > 0))
            {
                var extension = Path.GetExtension(file.Path).TrimStart('.');

                if (extension.IsNullOrWhiteSpace())
                {
                    continue;
                }

                string movedPath;

                try
                {
                    movedPath = _calibreProxy.GetFormatLocalPath(file.CalibreId, extension, settings);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to read calibre record {0} back after pushing identity fields", file.CalibreId);
                    continue;
                }

                if (movedPath == null || movedPath.PathEquals(file.Path))
                {
                    continue;
                }

                _logger.Info("Calibre refiled '{0}' to '{1}'", file.Path, movedPath);
                file.Path = movedPath;
                _mediaFileService.Update(file);
            }
        }

        private string ResolveTitle(List<BookFile> files, Book book)
        {
            var editionTitle = files
                .Select(f => f.Edition?.Title)
                .FirstOrDefault(t => t.IsNotNullOrWhiteSpace());

            if (editionTitle.IsNotNullOrWhiteSpace())
            {
                return editionTitle;
            }

            var monitored = _editionService.GetEditionsByBook(book.Id)?
                .FirstOrDefault(e => e.Monitored && e.Title.IsNotNullOrWhiteSpace());

            return monitored?.Title ?? book.Title;
        }

        private bool TryGetCalibreSettings(string path, out CalibreSettings settings)
        {
            settings = null;

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", path);
                return false;
            }

            if (rootFolder == null || !rootFolder.IsCalibreLibrary || rootFolder.CalibreSettings == null)
            {
                return false;
            }

            settings = rootFolder.CalibreSettings;
            return true;
        }
    }
}
