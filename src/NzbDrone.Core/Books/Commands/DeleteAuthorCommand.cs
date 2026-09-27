using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class DeleteAuthorCommand : Command
    {
        public List<int> AuthorIds { get; set; }
        public bool DeleteFiles { get; set; }
        public bool AddImportListExclusion { get; set; }

        public DeleteAuthorCommand()
        {
        }

        public DeleteAuthorCommand(List<int> authorIds, bool deleteFiles, bool addImportListExclusion = false)
        {
            AuthorIds = authorIds;
            DeleteFiles = deleteFiles;
            AddImportListExclusion = addImportListExclusion;
        }

        public override bool SendUpdatesToClient => true;
        public override bool IsLongRunning => true;

        // Only contend with other disk-access commands (moves, renames, ...) when this delete
        // will actually touch the author's files - a metadata-only delete shouldn't be serialized
        // behind unrelated disk work. See PR #188 for why this group exists.
        public override bool RequiresDiskAccess => DeleteFiles;
    }
}
