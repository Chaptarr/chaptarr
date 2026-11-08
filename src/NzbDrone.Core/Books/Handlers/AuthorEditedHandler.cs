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
            if (message.IdentityReconciliation) return;
            _logger.Debug("[FLOW-DEBUG] ========== AuthorEditedService.Handle START ==========");
            _logger.Debug("[FLOW-DEBUG] Author: '{0}' (ID: {1})", message.Author.Name, message.Author.Id);
            _logger.Debug("[FLOW-DEBUG] Old MetadataProfileId: {0}, New MetadataProfileId: {1}", message.OldAuthor.MetadataProfileId, message.Author.MetadataProfileId);

            // Check if author gained a new root folder type
            var gainedAudiobookFolder = string.IsNullOrWhiteSpace(message.OldAuthor.AudiobookRootFolderPath) &&
                                       !string.IsNullOrWhiteSpace(message.Author.AudiobookRootFolderPath);
            var gainedEbookFolder = string.IsNullOrWhiteSpace(message.OldAuthor.EbookRootFolderPath) &&
                                   !string.IsNullOrWhiteSpace(message.Author.EbookRootFolderPath);
            var gainedRootFolder = gainedAudiobookFolder || gainedEbookFolder;

            var metadataProfileChanged = message.Author.MetadataProfileId != message.OldAuthor.MetadataProfileId;

            // Initial format setup already owns catalog loading and the following scan.
            // Ordinary root edits without that saved setup intent still need hydration.
            if (!metadataProfileChanged && gainedRootFolder &&
                (!gainedAudiobookFolder || message.Author.AddOptions?.AudiobookMonitor.HasValue == true) &&
                (!gainedEbookFolder || message.Author.AddOptions?.EbookMonitor.HasValue == true))
            {
                _logger.Debug("Author {0} is initializing its added format; retaining the existing catalog/scan handoff", message.Author.Id);
                return;
            }


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
