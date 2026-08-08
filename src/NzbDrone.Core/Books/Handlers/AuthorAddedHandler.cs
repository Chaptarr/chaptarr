using NLog;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class AuthorAddedHandler : IHandle<AuthorAddedEvent>,
                                      IHandle<AuthorsImportedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public AuthorAddedHandler(IManageCommandQueue commandQueueManager, Logger logger)
        {
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Handle(AuthorAddedEvent message)
        {
            _logger.Debug("[FLOW-DEBUG] ========== AuthorAddedHandler.Handle START ==========");
            _logger.Debug("[FLOW-DEBUG] Author: '{0}' (ID: {1})", message.Author.Name, message.Author.Id);
            _logger.Debug("[FLOW-DEBUG] DoRefresh: {0}", message.DoRefresh);
            _logger.Debug("[FLOW-DEBUG] Author.AddOptions: {0}", message.Author.AddOptions != null ? "Present" : "NULL");

            if (message.DoRefresh)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Will queue RefreshAuthorCommand");
                _logger.Debug("[FLOW-DEBUG] FLAGS: refreshMetadata=true, rescanFolders=true, isNewAuthor=true, isFromImport=false");
                _logger.Debug("[FLOW-DEBUG] REASON: New authors need both metadata refresh and folder scan to find their files");

                // New authors need both metadata refresh and folder scan to find their files
                _commandQueueManager.Push(new RefreshAuthorCommand(message.Author.Id, refreshMetadata: true, rescanFolders: true, isNewAuthor: true, isFromImport: false, forceRefresh: true));

                _logger.Debug("[FLOW-DEBUG] RefreshAuthorCommand pushed to queue");
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Skipping refresh as DoRefresh=false");
            }

            _logger.Debug("[FLOW-DEBUG] ========== AuthorAddedHandler.Handle END ==========");
        }

        public void Handle(AuthorsImportedEvent message)
        {
            _logger.Debug("[FLOW-DEBUG] ========== AuthorAddedHandler.Handle(AuthorsImportedEvent) START ==========");
            _logger.Debug("[FLOW-DEBUG] AuthorIds Count: {0}", message.AuthorIds.Count);
            _logger.Debug("[FLOW-DEBUG] DoRefresh: {0}", message.DoRefresh);

            if (message.DoRefresh)
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Will queue BulkRefreshAuthorCommand");
                _logger.Debug("[FLOW-DEBUG] FLAGS: refreshMetadata=true, rescanFolders=true (default for bulk), isFromImport=true");
                _logger.Debug("[FLOW-DEBUG] REASON: Bulk import of authors");

                _commandQueueManager.Push(new BulkRefreshAuthorCommand(message.AuthorIds, refreshMetadata: true, rescanFolders: true, areNewAuthors: true, trigger: CommandTrigger.Unspecified, isFromImport: true, forceRefresh: true));

                _logger.Debug("[FLOW-DEBUG] BulkRefreshAuthorCommand pushed to queue");
            }
            else
            {
                _logger.Debug("[FLOW-DEBUG] DECISION: Skipping bulk refresh as DoRefresh=false");
            }

            _logger.Debug("[FLOW-DEBUG] ========== AuthorAddedHandler.Handle(AuthorsImportedEvent) END ==========");
        }
    }
}
