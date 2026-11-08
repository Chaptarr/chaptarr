using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaCover
{
    /// <summary>
    /// Handles downloading covers after import completes.
    /// </summary>
    public class DeferredCoverDownloadService : IHandleAsync<BookImportedEvent>,
                                                IHandleAsync<ImportStageProgressEvent>,
                                                IHandle<CommandExecutedEvent>,
                                                IHandle<BookDeletedEvent>,
        IHandle<LibraryIdentityChangedEvent>,
                                                IHandle<BookFileAddedEvent>,
                                                IHandle<BookFilesAddedEvent>,
                                                IHandle<ConfigSavedEvent>,
                                                IHandle<BooksEditedEvent>
    {
        private const int CompletedCommandLimit = 1024;

        private readonly IDeferredCoverService _deferredCoverService;
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly Logger _logger;
        private readonly object _importStateLock = new object();
        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();
        private readonly HashSet<int> _completedImportCommands = new HashSet<int>();
        private readonly Queue<int> _completedImportCommandOrder = new Queue<int>();

        public DeferredCoverDownloadService(
            IDeferredCoverService deferredCoverService,
            IBookService bookService,
            IManageCommandQueue commandQueueManager,
            IMapCoversToLocal mediaCoverService,
            Logger logger)
        {
            _deferredCoverService = deferredCoverService;
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
            _mediaCoverService = mediaCoverService;
            _logger = logger;
        }

        public void HandleAsync(BookImportedEvent message)
        {
            // The file-added event normally supplies this work; retain the import's book
            // when imports are deferred so completion can process it with the same batch.
            if (_deferredCoverService.IsCoverDownloadDeferred)
            {
                if (message?.Book?.Id > 0)
                {
                    _deferredCoverService.MarkBookForCoverDownload(message.Book.Id);
                }

                return;
            }

            FlushPendingBookCovers("[PERF] Processing deferred cover downloads for {0} books after import");
        }

        public void Handle(BookFileAddedEvent message)
        {
            QueueOrDeferBookCovers(GetBookIdsForFiles(new[] { message?.BookFile }));
        }

        public void Handle(BookFilesAddedEvent message)
        {
            QueueOrDeferBookCovers(GetBookIdsForFiles(message?.BookFiles));
        }

        public void Handle(ConfigSavedEvent message)
        {
            if (message.BookCoverCleanupRequired)
            {
                _commandQueueManager.Push(new CleanupBookCoversCommand());
            }
        }

        public void Handle(BooksEditedEvent message)
        {
            var bookIds = message?.Changes?
                .Where(change => BecameMonitored(change))
                .Select(change => change.Book.Id);

            QueueOrDeferBookCovers(bookIds);
        }

        private static bool BecameMonitored(BookEditedEvent change)
        {
            return change?.Book?.Id > 0 &&
                   change.OldBook != null &&
                   ((!change.OldBook.AudiobookMonitored && change.Book.AudiobookMonitored) ||
                    (!change.OldBook.EbookMonitored && change.Book.EbookMonitored));
        }

        private List<int> GetBookIdsForFiles(IEnumerable<BookFile> bookFiles)
        {
            var files = bookFiles?.Where(file => file != null).ToList() ?? new List<BookFile>();
            var bookIds = new HashSet<int>();
            var unresolvedFileIds = new List<int>();
            foreach (var file in files)
            {
                var bookId = file.LazyEdition?.IsLoaded == true ? file.Edition?.BookId ?? 0 : 0;
                if (bookId > 0)
                {
                    bookIds.Add(bookId);
                }
                else if (file.Id > 0)
                {
                    unresolvedFileIds.Add(file.Id);
                }
            }

            unresolvedFileIds = unresolvedFileIds.Distinct().ToList();

            if (unresolvedFileIds.Count > 0)
            {
                try
                {
                    bookIds.UnionWith(_bookService.GetBooksByFileIds(unresolvedFileIds)
                        .Where(book => book?.Id > 0)
                        .Select(book => book.Id));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to resolve books for {0} added files", unresolvedFileIds.Count);
                }
            }

            return bookIds.OrderBy(bookId => bookId).ToList();
        }

        private void QueueOrDeferBookCovers(IEnumerable<int> bookIds)
        {
            var ids = bookIds?.Where(bookId => bookId > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return;
            }

            try
            {
                if (_deferredCoverService.MarkBooksForCoverDownload(ids))
                {
                    return;
                }

                _commandQueueManager.Push(new DownloadBookMediaCommand(ids));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to queue cover downloads for {0} books", ids.Count);
            }
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

        public void Handle(LibraryIdentityChangedEvent message)
        {
            foreach (var bookId in message.RemovedBookIds) _deferredCoverService.RemovePendingBook(bookId);
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
            // Claim before processing so a later import can re-defer the same books.
            var pendingBookIds = _deferredCoverService.TakePendingBookIds();

            if (pendingBookIds.Count == 0)
            {
                return;
            }

            _logger.Info(logMessageTemplate, pendingBookIds.Count);

            try
            {
                var books = _bookService.GetExistingBooks(pendingBookIds);
                var missingCount = pendingBookIds.Count - books.Count;
                if (missingCount > 0)
                {
                    _logger.Debug("Skipping deferred cover downloads for {0} deleted books", missingCount);
                }

                if (books.Count > 0)
                {
                    _mediaCoverService.EnsureBookCoversForBooks(books);
                }

                _logger.Debug("[PERF] Processed deferred cover batch for {0} books", books.Count);
            }
            catch (Exception ex)
            {
                _deferredCoverService.RequeueBooksForCoverDownload(pendingBookIds);
                _logger.Error(ex, "Deferred cover batch failed; retained {0} book IDs for a later flush", pendingBookIds.Count);
            }
        }
    }
}
