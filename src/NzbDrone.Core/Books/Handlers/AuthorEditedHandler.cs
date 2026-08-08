using System.Linq;
using NLog;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class AuthorEditedService : IHandle<AuthorEditedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public AuthorEditedService(IManageCommandQueue commandQueueManager, IBookService bookService, Logger logger)
        {
            _commandQueueManager = commandQueueManager;
            _bookService = bookService;
            _logger = logger;
        }

        public void Handle(AuthorEditedEvent message)
        {
            _logger.Debug("[FLOW-DEBUG] ========== AuthorEditedService.Handle START ==========");
            _logger.Debug("[FLOW-DEBUG] Author: '{0}' (ID: {1})", message.Author.Name, message.Author.Id);
            _logger.Debug("[FLOW-DEBUG] Old MetadataProfileId: {0}, New MetadataProfileId: {1}", message.OldAuthor.MetadataProfileId, message.Author.MetadataProfileId);

            // Check if author gained a new root folder type
            var gainedAudiobookFolder = string.IsNullOrWhiteSpace(message.OldAuthor.AudiobookRootFolderPath) &&
                                       !string.IsNullOrWhiteSpace(message.Author.AudiobookRootFolderPath);
            var gainedEbookFolder = string.IsNullOrWhiteSpace(message.OldAuthor.EbookRootFolderPath) &&
                                   !string.IsNullOrWhiteSpace(message.Author.EbookRootFolderPath);
            var gainedRootFolder = gainedAudiobookFolder || gainedEbookFolder;

            if (gainedRootFolder)
            {
                _logger.Debug("[BOOK-MONITORING] Author '{0}' gained new root folder - updating book monitoring", message.Author.Name);

                if (gainedAudiobookFolder)
                {
                    _logger.Debug("[BOOK-MONITORING] Author gained audiobook folder: {0}", message.Author.AudiobookRootFolderPath);
                    UpdateBooksForNewRootFolder(message.Author, BookMediaType.Audiobook);
                }

                if (gainedEbookFolder)
                {
                    _logger.Debug("[BOOK-MONITORING] Author gained ebook folder: {0}", message.Author.EbookRootFolderPath);
                    UpdateBooksForNewRootFolder(message.Author, BookMediaType.Ebook);
                }
            }

            var metadataProfileChanged = message.Author.MetadataProfileId != message.OldAuthor.MetadataProfileId;

            // Refresh metadata when profile filters change, or when a per-format root is
            // explicitly added for the first time. The root-folder case is intentionally
            // bounded to blank -> set transitions so normal author/library refreshes keep
            // using the existing diff/ETag path.
            if (metadataProfileChanged || gainedRootFolder)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: MetadataProfileChanged={0}, GainedRootFolder={1} - will queue RefreshAuthorCommand", metadataProfileChanged, gainedRootFolder);
                _logger.Debug("[FLOW-DEBUG] FLAGS: refreshMetadata=true, rescanFolders=false, isNewAuthor=false, forceRefresh=True");
                _logger.Debug("[FLOW-DEBUG] REASON: Metadata hydration only; no folder rescan required");

                _commandQueueManager.Push(new RefreshAuthorCommand(message.Author.Id, refreshMetadata: true, rescanFolders: false, isNewAuthor: false, forceRefresh: true));

                _logger.Debug("[FLOW-DEBUG] RefreshAuthorCommand pushed to queue");
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: No metadata profile or root-folder hydration changes - skipping refresh");
            }

            _logger.Debug("[FLOW-DEBUG] ========== AuthorEditedService.Handle END ==========");
        }

        private void UpdateBooksForNewRootFolder(Author author, BookMediaType mediaType)
        {
            var allBooks = _bookService.GetBooksByAuthor(author.Id);
            var booksToUpdate = allBooks.Where(b => b.MediaType == mediaType).ToList();

            if (!booksToUpdate.Any())
            {
                _logger.Debug("[BOOK-MONITORING] No {0} books found for author", mediaType);
                return;
            }

            var monitoringValue = mediaType == BookMediaType.Audiobook
                ? author.AudiobookMonitorExisting
                : author.EbookMonitorExisting;

            // If monitoring value is null, skip - don't assume anything
            if (!monitoringValue.HasValue)
            {
                _logger.Debug("[BOOK-MONITORING] No monitoring value set for {0}, skipping", mediaType);
                return;
            }

            _logger.Debug("[BOOK-MONITORING] Updating {0} {1} books with monitoring mode={2} (0=None, 1=All, 2=Selected)",
                booksToUpdate.Count, mediaType, monitoringValue.Value);

            foreach (var book in booksToUpdate)
            {
                // Apply tri-state monitoring logic
                if (monitoringValue.Value == 1) // All mode - monitor everything
                {
                    if (mediaType == BookMediaType.Audiobook && !book.AudiobookMonitored)
                    {
                        book.AudiobookMonitored = true;
                        _logger.Debug("[BOOK-MONITORING] Updated audiobook '{0}' to monitored=true (All mode)", book.Title);
                    }
                    else if (mediaType == BookMediaType.Ebook && !book.EbookMonitored)
                    {
                        book.EbookMonitored = true;
                        _logger.Debug("[BOOK-MONITORING] Updated ebook '{0}' to monitored=true (All mode)", book.Title);
                    }
                }
                // Note: Selected mode (2) and None mode (0) don't auto-update monitoring
            }

            // Update all books in a single batch
            _bookService.UpdateMany(booksToUpdate);
            _logger.Debug("[BOOK-MONITORING] Successfully updated {0} {1} books", booksToUpdate.Count, mediaType);
        }
    }
}
