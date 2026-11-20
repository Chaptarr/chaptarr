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

        // Only queues conversion jobs and returns. Encoding runs on the conversion worker and
        // the import runs as ImportLibraryConversionCommand, so this never holds disk access
        // or a command thread while a book converts.
        public override bool SendUpdatesToClient => true;

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
