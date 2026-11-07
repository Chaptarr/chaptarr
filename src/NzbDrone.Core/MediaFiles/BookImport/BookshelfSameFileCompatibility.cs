using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class BookshelfSameFileCompatibility
    {
        internal const string RejectionReason = "Has the same filesize as existing file";

        public static bool HasSameSizeCompatibleFile(LocalBook incoming, IEnumerable<BookFile> existingFiles)
        {
            if (incoming == null ||
                incoming.Path.IsNullOrWhiteSpace() ||
                incoming.IsLibraryConversion ||
                incoming.IsGeneratedConversion)
            {
                return false;
            }

            return existingFiles?.Any(existing =>
                existing != null &&
                existing.Path.IsNotNullOrWhiteSpace() &&
                existing.Size == incoming.Size &&
                MediaFileExtensions.AreCompatibleFormats(
                    existing.Quality?.Quality,
                    existing.Path,
                    incoming.Quality?.Quality,
                    incoming.Path)) == true;
        }
    }
}
