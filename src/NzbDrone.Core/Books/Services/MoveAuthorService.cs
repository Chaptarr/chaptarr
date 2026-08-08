using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.Books
{
    public class MoveAuthorService : IExecute<MoveAuthorCommand>, IExecute<BulkMoveAuthorCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public MoveAuthorService(IAuthorService authorService,
                                 IBuildFileNames filenameBuilder,
                                 IDiskProvider diskProvider,
                                 IRootFolderWatchingService rootFolderWatchingService,
                                 IDiskTransferService diskTransferService,
                                 IEventAggregator eventAggregator,
                                 Logger logger)
        {
            _authorService = authorService;
            _filenameBuilder = filenameBuilder;
            _diskProvider = diskProvider;
            _rootFolderWatchingService = rootFolderWatchingService;
            _diskTransferService = diskTransferService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private bool MoveSingleAuthor(Author author, string sourcePath, string destinationPath, string mediaType = null, int? index = null, int? total = null)
        {
            if (!_diskProvider.FolderExists(sourcePath))
            {
                _logger.Debug("Folder '{0}' for '{1}' does not exist, not moving.", sourcePath, author.Name);
                return false;
            }

            if (index != null && total != null)
            {
                _logger.ProgressInfo("Moving {0} from '{1}' to '{2}' ({3}/{4})", author.Name, sourcePath, destinationPath, index + 1, total);
            }
            else
            {
                _logger.ProgressInfo("Moving {0} from '{1}' to '{2}'", author.Name, sourcePath, destinationPath);
            }

            if (sourcePath.PathEquals(destinationPath))
            {
                _logger.ProgressInfo("{0} is already in the specified location '{1}'.", author, destinationPath);
                return false;
            }

            try
            {
                _rootFolderWatchingService.ReportFileSystemChangeBeginning(sourcePath, destinationPath);

                _diskTransferService.TransferFolder(sourcePath, destinationPath, TransferMode.Move);

                _logger.ProgressInfo("{0} moved successfully to {1}", author.Name, destinationPath);

                _eventAggregator.PublishEvent(new AuthorMovedEvent(author, sourcePath, destinationPath));
                return true;
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "Unable to move author from '{0}' to '{1}'. Try moving files manually", sourcePath, destinationPath);

                RevertPathIfNeeded(author.Id, sourcePath, destinationPath, mediaType);
                return false;
            }
        }

        private void RevertPathIfNeeded(int authorId, string sourcePath, string destinationPath, string mediaType)
        {
            var author = _authorService.GetAuthor(authorId);
            var updated = false;

            // In a dual-media-type system, bulk moves can move a non-primary folder (e.g. ebooks)
            // while author.Path still points at the primary folder (e.g. audiobooks). Only revert
            // author.Path when it was previously updated to the destination.
            if (author.Path.PathEquals(destinationPath))
            {
                author.Path = sourcePath;
                updated = true;
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(author.EbookPath) && author.EbookPath.PathEquals(destinationPath))
                {
                    author.EbookPath = sourcePath;
                    updated = true;
                }
            }
            else if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(author.AudiobookPath) && author.AudiobookPath.PathEquals(destinationPath))
                {
                    author.AudiobookPath = sourcePath;
                    updated = true;
                }
            }

            if (updated)
            {
                _authorService.UpdateAuthor(author);
            }
        }

        public void Execute(MoveAuthorCommand message)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            MoveSingleAuthor(author, message.SourcePath, message.DestinationPath);
        }

        public void Execute(BulkMoveAuthorCommand message)
        {
            var authorToMove = message.Author;
            var destinationRootFolder = message.DestinationRootFolder;

            _logger.ProgressInfo("Moving {0} author to '{1}'", authorToMove.Count, destinationRootFolder);

            for (var index = 0; index < authorToMove.Count; index++)
            {
                var s = authorToMove[index];
                var author = _authorService.GetAuthor(s.AuthorId);
                var mediaType = s.MediaType.IsNullOrWhiteSpace() ? "audiobook" : s.MediaType;
                var destinationPath = Path.Combine(destinationRootFolder, _filenameBuilder.GetAuthorFolder(author, mediaType: mediaType));

                var moved = MoveSingleAuthor(author, s.SourcePath, destinationPath, mediaType, index, authorToMove.Count);
                if (moved)
                {
                    UpdateAuthorPathsAfterMove(author, s.SourcePath, destinationPath, mediaType);
                }
            }

            _logger.ProgressInfo("Finished moving {0} author to '{1}'", authorToMove.Count, destinationRootFolder);
        }

        private void UpdateAuthorPathsAfterMove(Author author, string sourcePath, string destinationPath, string mediaType)
        {
            if (author == null)
            {
                return;
            }

            var updated = false;

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                if (!destinationPath.PathEquals(author.EbookPath))
                {
                    author.EbookPath = destinationPath;
                    updated = true;
                }
            }
            else
            {
                if (!destinationPath.PathEquals(author.AudiobookPath))
                {
                    author.AudiobookPath = destinationPath;
                    updated = true;
                }
            }

            // Keep legacy primary Path aligned if it pointed at the moved folder.
            if (author.Path.PathEquals(sourcePath))
            {
                author.Path = destinationPath;
                updated = true;
            }

            if (updated)
            {
                _authorService.UpdateAuthor(author);
            }
        }
    }
}
