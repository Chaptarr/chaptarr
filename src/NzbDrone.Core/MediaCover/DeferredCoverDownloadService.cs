using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    /// <summary>
    /// Handles downloading covers after import completes.
    /// </summary>
    public class DeferredCoverDownloadService : IHandleAsync<BookImportedEvent>,
                                                IHandleAsync<ImportStageProgressEvent>,
                                                IHandle<CommandExecutedEvent>,
                                                IHandle<BookDeletedEvent>
    {
        private const int CompletedCommandLimit = 1024;

        private readonly IDeferredCoverService _deferredCoverService;
        private readonly IBookService _bookService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly Logger _logger;
        private readonly object _importStateLock = new object();
        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();
        private readonly HashSet<int> _completedImportCommands = new HashSet<int>();
        private readonly Queue<int> _completedImportCommandOrder = new Queue<int>();

        public DeferredCoverDownloadService(
            IDeferredCoverService deferredCoverService,
            IBookService bookService,
            IMapCoversToLocal mediaCoverService,
            Logger logger)
        {
            _deferredCoverService = deferredCoverService;
            _bookService = bookService;
            _mediaCoverService = mediaCoverService;
            _logger = logger;
        }

        public void HandleAsync(BookImportedEvent message)
        {
            // If we're deferring, just keep collecting pending book IDs.
            // We'll process them when the import completes.
            if (_deferredCoverService.IsCoverDownloadDeferred)
            {
                return;
            }

            FlushPendingBookCovers("[PERF] Processing deferred cover downloads for {0} books after import");
        }

        public void HandleAsync(ImportStageProgressEvent message)
        {
            if (!message.CommandId.HasValue)
            {
                return;
            }

            var shouldFlush = false;

            lock (_importStateLock)
            {
                if (_completedImportCommands.Contains(message.CommandId.Value))
                {
                    return;
                }

                if (message.Stage == ImportStage.ImportComplete)
                {
                    _activeImportCommands.Remove(message.CommandId.Value);
                    RememberCompletedImportCommand(message.CommandId.Value);
                    shouldFlush = _activeImportCommands.Count == 0;
                }
                else
                {
                    _activeImportCommands.Add(message.CommandId.Value);
                }

                _deferredCoverService.IsCoverDownloadDeferred = _activeImportCommands.Count > 0;
            }

            if (message.Stage == ImportStage.ImportComplete && shouldFlush)
            {
                // Import is done; allow cover downloads and flush anything that was deferred.
                FlushPendingBookCovers("[PERF] Import complete - flushing deferred cover downloads for {0} books");
            }
        }

        public void Handle(CommandExecutedEvent message)
        {
            var commandId = message?.Command?.Id ?? 0;
            if (commandId <= 0)
            {
                return;
            }

            var shouldFlush = false;
            lock (_importStateLock)
            {
                RememberCompletedImportCommand(commandId);

                if (_activeImportCommands.Remove(commandId))
                {
                    shouldFlush = _activeImportCommands.Count == 0;
                    _deferredCoverService.IsCoverDownloadDeferred = _activeImportCommands.Count > 0;
                }
            }

            if (shouldFlush)
            {
                FlushPendingBookCovers("[PERF] Import command complete - flushing deferred cover downloads for {0} books");
            }
        }

        private void RememberCompletedImportCommand(int commandId)
        {
            if (!_completedImportCommands.Add(commandId))
            {
                return;
            }

            _completedImportCommandOrder.Enqueue(commandId);

            while (_completedImportCommandOrder.Count > CompletedCommandLimit)
            {
                _completedImportCommands.Remove(_completedImportCommandOrder.Dequeue());
            }
        }

        public void Handle(BookDeletedEvent message)
        {
            var bookId = message?.Book?.Id ?? 0;

            if (bookId <= 0)
            {
                return;
            }

            _deferredCoverService.RemovePendingBook(bookId);
        }

        private void FlushPendingBookCovers(string logMessageTemplate)
        {
            var pendingBookIds = _deferredCoverService.GetPendingBookIds()
                .Distinct()
                .ToList();

            if (!pendingBookIds.Any())
            {
                return;
            }

            _logger.Info(logMessageTemplate, pendingBookIds.Count);

            var books = _bookService.GetExistingBooks(pendingBookIds);
            var booksById = books.ToDictionary(book => book.Id);
            var missingBookIds = pendingBookIds
                .Where(bookId => !booksById.ContainsKey(bookId))
                .ToList();

            if (missingBookIds.Any())
            {
                _logger.Debug("Skipping deferred cover downloads for {0} deleted books", missingBookIds.Count);
            }

            foreach (var book in books)
            {
                try
                {
                    _mediaCoverService.EnsureBookCovers(book);
                    _logger.Debug("Downloaded covers for book {0}: {1}", book.Id, book.Title);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error downloading covers for book {0}", book.Id);
                }
            }

            // Remove only the snapshot we processed. A new import may have queued more books
            // while covers were being downloaded.
            _deferredCoverService.RemovePendingBooks(pendingBookIds);
            _logger.Debug("[PERF] Completed deferred cover downloads");
        }
    }
}
