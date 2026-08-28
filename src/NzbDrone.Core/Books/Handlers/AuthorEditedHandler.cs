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
        private readonly Logger _logger;

        public AuthorEditedService(IManageCommandQueue commandQueueManager, Logger logger)
        {
            _commandQueueManager = commandQueueManager;
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
                // Root-folder linkage does not rewrite independent book-row flags.
                // Any one-time catalog seed is applied by the add/import request;
                // the author gate only participates in eligibility.
                _logger.Debug("[BOOK-MONITORING] Author '{0}' gained a root folder; preserving existing book-row monitoring", message.Author.Name);
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

    }
}
