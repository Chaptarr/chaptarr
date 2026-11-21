using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    /// <summary>
    /// Converts every eligible book file for the selected authors to the conversion target
    /// configured on their quality profiles. The bulk counterpart to
    /// <see cref="ConvertBookFilesCommand"/>, scoped like <see cref="RenameAuthorCommand"/>.
    /// </summary>
    public class ConvertAuthorCommand : Command
    {
        public List<int> AuthorIds { get; set; }

        // Only queues conversion jobs and returns. Encoding runs on the conversion worker and
        // the import runs as ImportLibraryConversionCommand, so this never holds disk access
        // or a command thread while a book converts.
        public override bool SendUpdatesToClient => true;

        public ConvertAuthorCommand()
        {
            AuthorIds = new List<int>();
        }
    }
}
