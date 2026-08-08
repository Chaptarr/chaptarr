using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class BulkRefreshAuthorCommand : Command
    {
        public BulkRefreshAuthorCommand()
        {
            AuthorIds = new List<int>();

            // Default to full refresh for backward compatibility
            RefreshMetadata = true;
            RescanFolders = true;
        }

        public BulkRefreshAuthorCommand(List<int> authorIds, bool areNewAuthors = false, CommandTrigger trigger = CommandTrigger.Unspecified, bool isFromImport = false, bool forceRefresh = false)
        {
            AuthorIds = authorIds ?? new List<int>();
            AreNewAuthors = areNewAuthors;
            Trigger = trigger;
            IsFromImport = isFromImport;
            ForceRefresh = forceRefresh;

            // Default to full refresh for backward compatibility
            RefreshMetadata = true;
            RescanFolders = true;
        }

        // New constructor for granular control
        public BulkRefreshAuthorCommand(List<int> authorIds, bool refreshMetadata, bool rescanFolders, bool areNewAuthors = false, CommandTrigger trigger = CommandTrigger.Unspecified, bool isFromImport = false, bool forceRefresh = false)
        {
            AuthorIds = authorIds ?? new List<int>();
            RefreshMetadata = refreshMetadata;
            RescanFolders = rescanFolders;
            AreNewAuthors = areNewAuthors;
            Trigger = trigger;
            IsFromImport = isFromImport;
            ForceRefresh = forceRefresh;
        }

        public List<int> AuthorIds { get; set; }
        public string MediaType { get; set; } // "all", "audiobook", or "ebook"
        public bool AreNewAuthors { get; set; }
        public bool IsFromImport { get; set; }

        // New granular control flags
        public bool RefreshMetadata { get; set; }
        public bool RescanFolders { get; set; }
        public bool ForceRefresh { get; set; }

        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => false;
    }
}
