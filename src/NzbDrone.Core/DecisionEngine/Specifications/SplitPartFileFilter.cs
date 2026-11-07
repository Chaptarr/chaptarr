using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    /// <summary>
    /// Narrows the files a release is compared against for upgrade, cutoff and "upgrade allowed"
    /// checks. A release that is one piece of a split book is only an upgrade of the same piece, so
    /// sibling pieces already on disk must not make it look like an unnecessary duplicate.
    /// </summary>
    internal static class SplitPartFileFilter
    {
        public static IEnumerable<BookFile> ComparableFiles(RemoteBook subject, IEnumerable<BookFile> existingFiles)
        {
            var incomingPart = subject?.PackDetection?.SplitPart?.Index ?? 0;
            if (incomingPart <= 0 || existingFiles == null)
            {
                return existingFiles;
            }

            // A complete copy of the book (no release part) still counts, as does the same piece.
            return existingFiles.Where(f => f == null || f.ReleasePart <= 0 || f.ReleasePart == incomingPart);
        }
    }
}
