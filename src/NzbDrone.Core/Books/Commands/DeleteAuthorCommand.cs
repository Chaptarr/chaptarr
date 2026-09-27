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
        // group at all. When it does apply, it lands in the same "default" group every other
        // RequiresDiskAccess command uses via Command's own default DiskAccessGroup (MoveAuthorCommand,
        // RenameAuthorCommand, BulkMoveAuthorCommand, RescanFoldersCommand, ManualImportCommand, ...),
        // so CommandQueue's disk-access serialization (see PR #188) already keeps this from running
        // concurrently with a move/rename touching the same author's files.
        public override bool RequiresDiskAccess => DeleteFiles;
    }
}
