using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RescanFoldersCommand : Command
    {
        public RescanFoldersCommand()
        {
            // These are the settings used in the scheduled task
            Filter = FilterFilesType.Known;
            IsInitialImport = false;
        }

        public RescanFoldersCommand(List<string> folders, FilterFilesType filter, List<int> authorIds)
        {
            Folders = folders;
            Filter = filter;
            AuthorIds = authorIds;
            IsInitialImport = false;
        }

        public RescanFoldersCommand(List<string> folders, FilterFilesType filter, List<int> authorIds, bool isInitialImport)
        {
            Folders = folders;
            Filter = filter;
            AuthorIds = authorIds;
            IsInitialImport = isInitialImport;
        }

        public List<string> Folders { get; set; }
        public FilterFilesType Filter { get; set; }
        public List<int> AuthorIds { get; set; }
        public bool IsInitialImport { get; set; }
        public string MediaType { get; set; } // "all", "audiobook", or "ebook"
        public List<string> Paths { get; set; } // Specific file paths to scan
        public UnmappedFilesSelection UnmappedFiles { get; set; } // Server-side selector for unmapped file retries
        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }

    public class UnmappedFilesSelection
    {
        public string Scope { get; set; } // "all" or "selected"
        public List<int> BookFileIds { get; set; }
        public List<int> ExceptBookFileIds { get; set; }
    }

    public class RetryUnmappedMatchCommand : Command
    {
        public string MediaType { get; set; } // "all", "audiobook", or "ebook"
        public UnmappedFilesSelection UnmappedFiles { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }

    public class RefreshUnmappedFilesCommand : Command
    {
        public string MediaType { get; set; } // "all", "audiobook", or "ebook"
        public UnmappedFilesSelection UnmappedFiles { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }
}
