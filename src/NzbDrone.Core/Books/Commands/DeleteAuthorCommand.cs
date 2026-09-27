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

        // Scoped to DeleteFiles so a metadata-only delete isn't lumped into the "default" disk-access
        // group at all. Note this only self-serializes against other RequiresDiskAccess commands that
        // opt into the same group (see PR #188) - as of this PR that's just this command, so it does
        // not yet protect against a concurrent move/rename touching the same author's files. Widening
        // which commands opt in is a separate change.
        public override bool RequiresDiskAccess => DeleteFiles;
    }
}
