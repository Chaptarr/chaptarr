using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    /// <summary>
    /// Converts book files that are already in the library to the conversion target configured on
    /// the quality profile. Scoped the same way <see cref="RenameFilesCommand"/> is: an author, and
    /// optionally a single book or an explicit list of book file ids.
    /// </summary>
    public class ConvertBookFilesCommand : Command
    {
        public int AuthorId { get; set; }
        public int? BookId { get; set; }
        public List<int> Files { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
        public override bool IsLongRunning => true;

        public ConvertBookFilesCommand()
        {
        }

        public ConvertBookFilesCommand(int authorId, List<int> files, int? bookId = null)
        {
            AuthorId = authorId;
            Files = files;
            BookId = bookId;
        }
    }
}
