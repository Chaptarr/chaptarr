using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public class EbookColocateOnAudiobookImportHandler : IHandle<TrackImportedEvent>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public EbookColocateOnAudiobookImportHandler(IRootFolderService rootFolderService,
                                                     IBookService bookService,
                                                     IMediaFileService mediaFileService,
                                                     IMoveBookFiles bookFileMover,
                                                     IDiskProvider diskProvider,
                                                     IEventAggregator eventAggregator,
                                                     Logger logger)
        {
            _rootFolderService = rootFolderService;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _bookFileMover = bookFileMover;
            _diskProvider = diskProvider;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Handle(TrackImportedEvent message)
        {
            try
            {
                var imported = message?.ImportedBook;
                if (imported == null)
                {
                    return;
                }

                var mediaType = imported.MediaType;
                if (mediaType.IsNullOrWhiteSpace() && imported.Quality != null)
                {
                    mediaType = BookFile.DetermineMediaType(imported.Quality);
                }

                if (!string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var localBook = message.BookInfo;
                var author = localBook?.Author ?? imported.Author ?? imported.Edition?.Book?.Author;
                var audiobookBook = localBook?.Book ?? imported.Edition?.Book;

                if (author == null || audiobookBook == null)
                {
                    return;
                }

                var rootFolderPath = author.GetRootFolderForQuality(imported.Quality.Quality);
                if (rootFolderPath.IsNullOrWhiteSpace())
                {
                    return;
                }

                var bestRoot = _rootFolderService.GetBestRootFolder(rootFolderPath);
                if (bestRoot == null || bestRoot.FolderType != FolderType.Mixed || !bestRoot.PlaceEbooksWithAudiobooks)
                {
                    return;
                }

                // Find ebook siblings for the same work (work-level provider ID intersection).
                var allBooks = _bookService.GetBooksByAuthorId(audiobookBook.AuthorId);
                var ebookBooks = allBooks
                    .Where(b => b != null && b.MediaType == BookMediaType.Ebook)
                    .Where(b => WorkIdMatcher.WorkProviderIdMatches(audiobookBook, b))
                    .ToList();

                if (ebookBooks.Count == 0)
                {
                    return;
                }

                var renamedFiles = new List<RenamedBookFile>();
                var cleanup = new List<(string SourceFolder, string SourceAuthorFolderPath)>();

                foreach (var ebookBook in ebookBooks)
                {
                    var ebookFiles = _mediaFileService.GetFilesByBook(ebookBook.Id)
                        .Where(f => f != null && f.CalibreId == 0)
                        .ToList();

                    foreach (var ebookFile in ebookFiles)
                    {
                        // One failed file must not suppress sidecar handling for completed moves.
                        try
                        {
                            if (ebookFile.Path.IsNullOrWhiteSpace() || !_diskProvider.FileExists(ebookFile.Path))
                            {
                                continue;
                            }

                            var previousPath = ebookFile.Path;
                            var previousAuthorPath = Extras.ExtraFilePathHelper.GetPreferredBasePath(author, ebookFile);

                            // Route through the shared planner so ebook naming stays consistent, while the folder is clamped to audiobook locations.
                            var plan = _bookFileMover.GetOrganizeDestination(ebookFile, author, false);
                            if (!plan.CanOrganize)
                            {
                                _logger.Warn("Skipping ebook colocation for {0}: {1}", previousPath, plan.SkipReason);
                                continue;
                            }

                            // Different roots can yield a naming-only plan; this event must only apply colocation.
                            if (plan.ReplicaPaths == null)
                            {
                                _logger.Debug("Skipping ebook colocation for {0}: colocation does not apply; leaving file unchanged", previousPath);
                                continue;
                            }

                            _bookFileMover.MoveBookFile(ebookFile, author, plan);
                            _mediaFileService.Update(ebookFile);

                            if (previousPath.PathNotEquals(ebookFile.Path))
                            {
                                var renamed = new RenamedBookFile
                                {
                                    BookFile = ebookFile,
                                    PreviousPath = previousPath,
                                    PreviousAuthorPath = previousAuthorPath
                                };

                                _logger.Debug("Co-located ebook after audiobook import: {0} -> {1}", previousPath, ebookFile.Path);
                                _eventAggregator.PublishEvent(new BookFileRenamedEvent(author, ebookFile, previousPath));
                                renamedFiles.Add(renamed);
                                cleanup.Add((Path.GetDirectoryName(previousPath), plan.SourceAuthorFolderPath));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "Failed to colocate ebook {0}; continuing with remaining files", ebookFile.Path);
                        }
                    }
                }

                if (renamedFiles.Count > 0)
                {
                    _eventAggregator.PublishEvent(new AuthorRenamedEvent(author, renamedFiles));
                    Extras.ExtraFilePathHelper.CleanupEmptySourceFolders(_diskProvider, _logger, cleanup);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to colocate ebooks after audiobook import");
            }
        }
    }
}
