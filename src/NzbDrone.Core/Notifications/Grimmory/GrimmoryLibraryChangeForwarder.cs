using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Notifications.Grimmory
{
    // Grimmory's database is remote, but with sidecar write-on-update enabled it rewrites
    // "<book>.metadata.json" (and "<book>.cover.jpg") next to the book after every edit, so
    // watching the root folders is the only change signal available.
    public class GrimmoryLibraryChangeForwarder :
        IHandle<ApplicationStartedEvent>,
        IHandle<ModelEvent<RootFolder>>,
        IHandle<ProviderUpdatedEvent<INotification>>,
        IDisposable
    {
        // With save-to-original-file enabled Grimmory rewrites the book alongside the sidecar
        // and AudioBookShelf rescans it ~30s later, overwriting anything pushed before that.
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(90);
        private static readonly string[] SidecarSuffixes = { ".metadata.json", ".cover.jpg" };

        private readonly INotificationFactory _notificationFactory;
        private readonly IGrimmoryProxy _proxy;
        private readonly IRootFolderService _rootFolderService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new ConcurrentDictionary<int, FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, byte> _pendingSidecars = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Timers.Timer _debounce;
        private readonly object _forwardLock = new object();

        public GrimmoryLibraryChangeForwarder(INotificationFactory notificationFactory,
                                              IGrimmoryProxy proxy,
                                              IRootFolderService rootFolderService,
                                              IMediaFileService mediaFileService,
                                              IEditionService editionService,
                                              IBookService bookService,
                                              Logger logger)
        {
            _notificationFactory = notificationFactory;
            _proxy = proxy;
            _rootFolderService = rootFolderService;
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _bookService = bookService;
            _logger = logger;

            _debounce = new System.Timers.Timer(DebounceDelay.TotalMilliseconds) { AutoReset = false };
            _debounce.Elapsed += (s, e) => ForwardPending();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            SyncWatchers();
        }

        public void Handle(ModelEvent<RootFolder> message)
        {
            SyncWatchers();
        }

        public void Handle(ProviderUpdatedEvent<INotification> message)
        {
            SyncWatchers();
        }

        public void Dispose()
        {
            _debounce.Dispose();

            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        private void SyncWatchers()
        {
            List<RootFolder> wanted;

            try
            {
                wanted = ForwardingSources().Any() ? _rootFolderService.All() : new List<RootFolder>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to evaluate Grimmory forwarding sources");
                return;
            }

            var wantedIds = wanted.Select(r => r.Id).ToHashSet();

            foreach (var stale in _watchers.Keys.Where(id => !wantedIds.Contains(id)).ToList())
            {
                if (_watchers.TryRemove(stale, out var watcher))
                {
                    watcher.Dispose();
                }
            }

            foreach (var rootFolder in wanted)
            {
                if (_watchers.ContainsKey(rootFolder.Id) || rootFolder.Path.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(rootFolder.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        InternalBufferSize = 65536
                    };

                    watcher.Filters.Add("*.metadata.json");
                    watcher.Filters.Add("*.cover.jpg");

                    watcher.Changed += (s, e) => QueueSidecar(e.FullPath);
                    watcher.Created += (s, e) => QueueSidecar(e.FullPath);
                    watcher.Renamed += (s, e) => QueueSidecar(e.FullPath);
                    watcher.Error += (s, e) =>
                    {
                        _logger.Debug(e.GetException(), "Grimmory sidecar watcher error for {0}; recreating", rootFolder.Path);

                        if (_watchers.TryRemove(rootFolder.Id, out var broken))
                        {
                            broken.Dispose();
                        }

                        SyncWatchers();
                    };

                    watcher.EnableRaisingEvents = true;

                    if (!_watchers.TryAdd(rootFolder.Id, watcher))
                    {
                        watcher.Dispose();
                    }
                    else
                    {
                        _logger.Debug("Watching {0} for Grimmory sidecar changes", rootFolder.Path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to watch {0} for Grimmory sidecar changes", rootFolder.Path);
                }
            }
        }

        public void QueueSidecar(string path)
        {
            if (path.IsNullOrWhiteSpace() || !SidecarSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            try
            {
                var bookFile = ResolveSidecarBookFile(path);
                var edition = bookFile == null ? null : _editionService.GetEdition(bookFile.EditionId);
                var book = edition == null ? null : _bookService.GetBook(edition.BookId);

                if (book != null && GrimmoryPushRegistry.ShouldSuppressSidecarEvent(book.Id))
                {
                    _logger.Debug("Sidecar change for '{0}' follows Chaptarr's own push; not forwarding back out", book.Title);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to check sidecar {0} for push echo; queueing it", path);
            }

            _pendingSidecars[path] = 1;
            _debounce.Stop();
            _debounce.Start();
        }

        public void ForwardPending()
        {
            lock (_forwardLock)
            {
                var pending = _pendingSidecars.Keys.ToList();
                _pendingSidecars.Clear();

                if (pending.Empty())
                {
                    return;
                }

                var sources = ForwardingSources();

                if (sources.Empty())
                {
                    return;
                }

                var forwardedBooks = new HashSet<int>();

                foreach (var sidecarPath in pending)
                {
                    try
                    {
                        ForwardSidecarChange(sidecarPath, sources, forwardedBooks);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to forward Grimmory edit signalled by {0}", sidecarPath);
                    }
                }
            }
        }

        private void ForwardSidecarChange(string sidecarPath, List<Grimmory> sources, HashSet<int> forwardedBooks)
        {
            var bookFile = ResolveSidecarBookFile(sidecarPath);

            if (bookFile == null)
            {
                _logger.Debug("No Chaptarr file matches sidecar {0}; skipping forward", sidecarPath);
                return;
            }

            var edition = _editionService.GetEdition(bookFile.EditionId);
            var book = edition == null ? null : _bookService.GetBook(edition.BookId);

            if (book == null || !forwardedBooks.Add(book.Id))
            {
                return;
            }

            var targets = _notificationFactory.GetAvailableProviders()
                .OfType<IExternalLibraryEditTarget>()
                .Where(t => t.AcceptsExternalLibraryEdits)
                .ToList();

            var files = _mediaFileService.GetFilesByBook(book.Id);

            foreach (var source in sources)
            {
                var settings = (GrimmorySettings)source.Definition.Settings;
                var libraryId = book.MediaType == BookMediaType.Ebook ? settings.EbookLibraryId : settings.AudiobookLibraryId;

                if (libraryId <= 0)
                {
                    continue;
                }

                var relativePath = GetRootRelativePath(bookFile.Path);

                if (relativePath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var grimmoryBook = _proxy.FindBookByPath(settings, libraryId, relativePath, bypassCache: true);

                if (grimmoryBook == null)
                {
                    continue;
                }

                var sourceTargets = targets.Where(t => t.Definition?.Id != source.Definition.Id).ToList();

                if (sourceTargets.Empty())
                {
                    _logger.Debug("Grimmory edit of '{0}' detected but no connections accept library edits", book.Title);
                    continue;
                }

                var payload = BuildPayload(settings, grimmoryBook);

                foreach (var target in sourceTargets)
                {
                    try
                    {
                        target.PushExternalLibraryEdit(book, files, payload);
                        _logger.Debug("Forwarded Grimmory edit of '{0}' to {1}", book.Title, target.Definition?.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to forward Grimmory edit of '{0}' to {1}", book.Title, target.Definition?.Name);
                    }
                }
            }
        }

        private List<Grimmory> ForwardingSources()
        {
            return _notificationFactory.GetAvailableProviders()
                .OfType<Grimmory>()
                .Where(g => (g.Definition?.Settings as GrimmorySettings)?.ForwardEdits == true)
                .ToList();
        }

        private BookFile ResolveSidecarBookFile(string sidecarPath)
        {
            var fileName = Path.GetFileName(sidecarPath);
            var suffix = SidecarSuffixes.FirstOrDefault(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            var directory = Path.GetDirectoryName(sidecarPath);

            if (suffix == null || directory.IsNullOrWhiteSpace())
            {
                return null;
            }

            var baseName = fileName.Substring(0, fileName.Length - suffix.Length);

            return _mediaFileService.GetFilesWithBasePath(directory)
                .FirstOrDefault(f => f?.Path.IsNotNullOrWhiteSpace() == true &&
                    (Path.GetFileNameWithoutExtension(f.Path).Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                     IsInsideBookFolder(f.Path, directory, baseName)));
        }

        // Grimmory writes a multi-file audiobook's sidecar beside the book folder, named after the
        // folder with anything after its last dot trimmed as though it were an extension.
        private static bool IsInsideBookFolder(string filePath, string directory, string baseName)
        {
            if (!directory.IsParentPath(filePath))
            {
                return false;
            }

            var segments = directory.GetRelativePath(filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (segments.Length < 2)
            {
                return false;
            }

            return segments[0].Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                   segments[0].StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase);
        }

        private string GetRootRelativePath(string path)
        {
            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            if (rootFolder?.Path == null || rootFolder.Path.PathEquals(path))
            {
                return null;
            }

            return rootFolder.Path.GetRelativePath(path);
        }

        private ExternalLibraryEditPayload BuildPayload(GrimmorySettings settings, GrimmoryBook grimmoryBook)
        {
            var metadata = grimmoryBook.Metadata;
            var payload = new ExternalLibraryEditPayload();

            if (metadata != null)
            {
                payload.Title = metadata.Title;
                payload.Subtitle = metadata.Subtitle;
                payload.Description = metadata.Description;
                payload.Publisher = metadata.Publisher;
                payload.SeriesName = metadata.SeriesName;
                payload.SeriesPosition = metadata.SeriesNumber;
                payload.Languages = metadata.Language.IsNotNullOrWhiteSpace() ? new List<string> { metadata.Language } : null;
                payload.Genres = metadata.Categories?.Any() == true ? metadata.Categories : null;

                if (DateTime.TryParse(metadata.PublishedDate, out var published))
                {
                    payload.PublishedDate = published;
                }

                var identifiers = new Dictionary<string, string>();

                if (metadata.Isbn13.IsNotNullOrWhiteSpace())
                {
                    identifiers["isbn"] = metadata.Isbn13;
                }

                if (metadata.Asin.IsNotNullOrWhiteSpace())
                {
                    identifiers["asin"] = metadata.Asin;
                }

                if (metadata.GoodreadsId.IsNotNullOrWhiteSpace())
                {
                    identifiers["goodreads"] = metadata.GoodreadsId;
                }

                if (identifiers.Any())
                {
                    payload.Identifiers = identifiers;
                }
            }

            try
            {
                payload.CoverBytes = _proxy.GetBookCover(settings, grimmoryBook);
                payload.CoverUrl = _proxy.BuildCoverUrl(settings, grimmoryBook);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not fetch Grimmory cover for book {0}", grimmoryBook.Id);
            }

            return payload;
        }
    }
}
