using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class RefreshAuthorCommand : Command
    {
        public int? AuthorId { get; set; }
        public bool IsNewAuthor { get; set; }
        public bool IsFromImport { get; set; }

        // New granular control flags
        public bool RefreshMetadata { get; set; }
        public bool RescanFolders { get; set; }
        // ForceRefresh is a local reconciliation request: fetch the latest
        // published author blob, bypass If-None-Match, and compare it to local DB.
        public bool ForceRefresh { get; set; }

        public RefreshAuthorCommand()
        {
            // Default to full refresh for backward compatibility
            RefreshMetadata = true;
            RescanFolders = true;
        }

        public RefreshAuthorCommand(int? authorId, bool isNewAuthor = false, bool isFromImport = false)
        {
            AuthorId = authorId;
            IsNewAuthor = isNewAuthor;
            IsFromImport = isFromImport;

            // Default to full refresh for backward compatibility
            RefreshMetadata = true;
            RescanFolders = true;
        }

        // New constructor for granular control
        public RefreshAuthorCommand(int? authorId, bool refreshMetadata, bool rescanFolders, bool isNewAuthor = false, bool isFromImport = false, bool forceRefresh = false)
        {
            AuthorId = authorId;
            RefreshMetadata = refreshMetadata;
            RescanFolders = rescanFolders;
            IsNewAuthor = isNewAuthor;
            IsFromImport = isFromImport;
            ForceRefresh = forceRefresh;
        }

        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => !AuthorId.HasValue;

        public override string CompletionMessage => "Completed";

        public override bool IsLongRunning => !AuthorId.HasValue;

        public override bool IsTypeExclusive => !AuthorId.HasValue;
    }
}
