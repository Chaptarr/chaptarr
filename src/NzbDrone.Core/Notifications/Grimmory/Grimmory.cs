using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class Grimmory : NotificationBase<GrimmorySettings>, IExternalLibraryEditTarget
    {
        private readonly IGrimmoryProxy _proxy;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;
        private readonly ICached<GrimmoryUpdateQueue> _pendingLibrariesCache;

        public Grimmory(IGrimmoryProxy proxy, IManageCommandQueue commandQueueManager, IRootFolderService rootFolderService, ICacheManager cacheManager, Logger logger)
        {
            _proxy = proxy;
            _commandQueueManager = commandQueueManager;
            _rootFolderService = rootFolderService;
            _logger = logger;
            _pendingLibrariesCache = cacheManager.GetRollingCache<GrimmoryUpdateQueue>(GetType(), "pendingLibraries", TimeSpan.FromDays(1));
        }

        public override string Name => "Grimmory";
        public override string Link => "https://github.com/grimmory-tools/grimmory";

        public override bool NotifyOnLibraryImports => Settings.PushMetadata || Settings.PushCovers;

        private class GrimmoryUpdateQueue
        {
            public HashSet<long> PendingLibraries { get; } = new HashSet<long>();
            public bool Refreshing { get; set; }
        }

        public override bool HasPendingQueue
        {
            get
            {
                var queue = _pendingLibrariesCache.Find(QueueKey);

                if (queue == null)
                {
                    return false;
                }

                lock (queue)
                {
                    return !queue.Refreshing && queue.PendingLibraries.Any();
                }
            }
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            if (message.BookFiles == null || message.BookFiles.Empty())
            {
                return;
            }

            QueueRefresh(GetLibraryId(message.Book, message.BookFiles.FirstOrDefault()), "import");

            // The push waits for Grimmory's (async) refresh to ingest the new files first.
            QueueAutoPush(message.Book, waitForBook: true);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            foreach (var renamedFile in renamedFiles ?? new List<RenamedBookFile>())
            {
                if (renamedFile?.BookFile != null)
                {
                    QueueRefresh(GetLibraryId(null, renamedFile.BookFile), "rename");
                }
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage message)
        {
            if (message.DeletedFiles)
            {
                QueueRefresh(Settings.EbookLibraryId, "author delete");
                QueueRefresh(Settings.AudiobookLibraryId, "author delete");
            }
        }

        public override void OnBookDelete(BookDeleteMessage message)
        {
            if (message.DeletedFiles)
            {
                QueueRefresh(GetLibraryId(message.Book, null), "book delete");
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage message)
        {
            QueueRefresh(GetLibraryId(message.Book, message.BookFile), "file delete");
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            QueueRefresh(GetLibraryId(message.Book, message.BookFile), "retag");
            QueueAutoPush(message.Book, waitForBook: false);
        }

        private void QueueAutoPush(Book book, bool waitForBook)
        {
            if (book == null || (!Settings.PushMetadata && !Settings.PushCovers))
            {
                return;
            }

            var fields = GrimmoryPushService.ToggleFields(Settings);

            if (fields.Empty())
            {
                return;
            }

            _commandQueueManager.Push(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { book.Id },
                Fields = fields,
                WaitForBook = waitForBook
            });
        }

        public override void ProcessQueue()
        {
            var queue = _pendingLibrariesCache.Find(QueueKey);

            if (queue == null)
            {
                return;
            }

            lock (queue)
            {
                if (queue.Refreshing)
                {
                    return;
                }

                queue.Refreshing = true;
            }

            try
            {
                while (true)
                {
                    List<long> libraryIds;

                    lock (queue)
                    {
                        if (queue.PendingLibraries.Empty())
                        {
                            queue.Refreshing = false;
                            return;
                        }

                        libraryIds = queue.PendingLibraries.ToList();
                        queue.PendingLibraries.Clear();
                    }

                    var failed = new List<long>();

                    foreach (var libraryId in libraryIds)
                    {
                        try
                        {
                            _proxy.RefreshLibrary(Settings, libraryId);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "Failed to trigger Grimmory refresh for library {0}", libraryId);
                            failed.Add(libraryId);
                        }
                    }

                    if (failed.Any())
                    {
                        throw new InvalidOperationException($"Failed to trigger Grimmory refresh for libraries: {string.Join(", ", failed)}");
                    }
                }
            }
            catch
            {
                lock (queue)
                {
                    queue.Refreshing = false;
                }

                throw;
            }
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getLibraries")
            {
                if (Settings.Url.IsNullOrWhiteSpace() || Settings.Username.IsNullOrWhiteSpace() || Settings.Password.IsNullOrWhiteSpace())
                {
                    return new { options = new List<object>() };
                }

                try
                {
                    var libraries = _proxy.GetLibraries(Settings);

                    return new
                    {
                        options = libraries
                            .OrderBy(l => l.Name, StringComparer.InvariantCultureIgnoreCase)
                            .Select(l => new
                            {
                                Value = l.Id,
                                Name = l.Name,
                                Hint = l.AllowedFormats?.Any() == true ? string.Join(", ", l.AllowedFormats) : "All formats"
                            })
                    };
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retrieve libraries from Grimmory");
                    return new { options = new List<object>() };
                }
            }

            return new { };
        }

        public bool AcceptsExternalLibraryEdits => Settings.PushMetadata || Settings.PushCovers;

        public void PushExternalLibraryEdit(Book book, List<BookFile> files, ExternalLibraryEditPayload payload)
        {
            if (book == null || payload == null || files == null || files.Empty())
            {
                return;
            }

            var libraryId = book.MediaType == BookMediaType.Ebook ? Settings.EbookLibraryId : Settings.AudiobookLibraryId;

            if (libraryId <= 0)
            {
                return;
            }

            var grimmoryBook = files
                .Select(f => GetRootRelativePath(f?.Path))
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => _proxy.FindBookByPath(Settings, libraryId, p, bypassCache: true))
                .FirstOrDefault(b => b != null);

            if (grimmoryBook == null)
            {
                return;
            }

            if (!payload.Manual && Settings.HasIgnoreTag(grimmoryBook.Metadata?.Tags))
            {
                _logger.Debug("'{0}' carries an ignore tag in Grimmory on {1}; not applying the forwarded edit", book.Title, Settings.Url);
                return;
            }

            var metadata = new Dictionary<string, object>();

            if (Settings.PushMetadata)
            {
                void Add(string field, object value)
                {
                    if (value != null && (!(value is string s) || s.IsNotNullOrWhiteSpace()))
                    {
                        metadata[field] = value;
                    }
                }

                Add("description", payload.Description);
                Add("publisher", payload.Publisher);
                Add("seriesName", payload.SeriesName);
                Add("seriesNumber", payload.SeriesPosition);
                Add("language", payload.Languages?.FirstOrDefault());
                Add("isbn13", payload.Identifiers?.GetValueOrDefault("isbn"));
                Add("asin", payload.Identifiers?.GetValueOrDefault("asin"));
                Add("goodreadsId", payload.Identifiers?.GetValueOrDefault("goodreads"));

                if (payload.PublishedDate.HasValue)
                {
                    metadata["publishedDate"] = payload.PublishedDate.Value.ToString("yyyy-MM-dd");
                }

                if (payload.Genres?.Any() == true)
                {
                    metadata["categories"] = payload.Genres;
                }

                if (metadata.Any())
                {
                    // Grimmory writes the sidecar during the update, so the entry has to
                    // exist before the call for the forwarder to absorb the echo.
                    GrimmoryPushRegistry.RecordPush(book.Id);
                    _proxy.UpdateBookMetadata(Settings, grimmoryBook.Id, metadata);
                }
            }

            if (Settings.PushCovers && payload.CoverBytes?.Length > 0)
            {
                try
                {
                    _proxy.UploadBookCover(Settings, grimmoryBook, payload.CoverBytes, "cover.jpg");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Grimmory rejected the forwarded cover for '{0}' on {1}", book.Title, Settings.Url);
                }
            }

            _logger.Debug("Applied external library edit of '{0}' to Grimmory book {1} on {2}", book.Title, grimmoryBook.Id, Settings.Url);
        }

        private string GetRootRelativePath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            if (rootFolder?.Path == null || rootFolder.Path.PathEquals(path))
            {
                return null;
            }

            return rootFolder.Path.GetRelativePath(path);
        }

        private string QueueKey => $"{Settings.Url}:{Settings.Username}";

        private void QueueRefresh(long libraryId, string reason)
        {
            if (libraryId <= 0)
            {
                return;
            }

            _logger.Debug("Grimmory: queueing refresh of library {0} after {1}", libraryId, reason);

            var queue = _pendingLibrariesCache.Get(QueueKey, () => new GrimmoryUpdateQueue());

            lock (queue)
            {
                queue.PendingLibraries.Add(libraryId);
            }
        }

        private long GetLibraryId(Book book, BookFile bookFile)
        {
            if (book != null)
            {
                return book.MediaType == BookMediaType.Ebook ? Settings.EbookLibraryId : Settings.AudiobookLibraryId;
            }

            var mediaType = bookFile?.MediaType;

            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            return mediaType switch
            {
                "ebook" => Settings.EbookLibraryId,
                "audiobook" => Settings.AudiobookLibraryId,
                _ => 0
            };
        }
    }
}
