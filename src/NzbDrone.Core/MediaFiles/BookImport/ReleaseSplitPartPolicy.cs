using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Keeps the parts of a split release side by side. A book published as "Part 1 of 2" and
    /// "Part 2 of 2" arrives as two downloads; importing one part must not treat the other
    /// part's files as an older copy of the book to upgrade away.
    /// </summary>
    public static class ReleaseSplitPartPolicy
    {
        public static ReleaseSplitPart GetSplitPart(LocalBook localBook)
        {
            return localBook?.ReleasePart > 0
                ? new ReleaseSplitPart(localBook.ReleasePart, localBook.ReleasePartCount)
                : null;
        }

        /// <summary>
        /// The split part a library file belongs to. Files imported before the part was recorded
        /// fall back to their release (scene) name and original download path.
        /// </summary>
        public static ReleaseSplitPart GetSplitPart(BookFile bookFile)
        {
            if (bookFile == null)
            {
                return null;
            }

            if (bookFile.ReleasePart > 0)
            {
                return new ReleaseSplitPart(bookFile.ReleasePart, bookFile.ReleasePartCount);
            }

            var split = ReleasePackDetector.DetectSplitPart(bookFile.SceneName);

            if (split == null && bookFile.OriginalFilePath.IsNotNullOrWhiteSpace())
            {
                var original = bookFile.OriginalFilePath.Replace('\\', '/');
                var folder = Path.GetDirectoryName(original)?.Replace('\\', '/');

                split = ReleasePackDetector.DetectSplitPart(Path.GetFileNameWithoutExtension(original), fileName: true) ??
                        (folder.IsNotNullOrWhiteSpace() ? ReleasePackDetector.DetectSplitPart(folder) : null);
            }

            return split;
        }

        /// <summary>
        /// True when <paramref name="existing"/> belongs to a different part of the split that
        /// <paramref name="incoming"/> is part of, so it must be kept rather than replaced.
        /// A whole-book file (no part) stays replaceable, as before.
        /// </summary>
        public static bool IsSiblingPart(BookFile existing, LocalBook incoming)
        {
            var incomingPart = GetSplitPart(incoming);
            return incomingPart != null && incomingPart.IsSiblingOf(GetSplitPart(existing));
        }

        /// <summary>
        /// The split part a release being considered for download belongs to, as found by pack detection.
        /// </summary>
        public static ReleaseSplitPart GetSplitPart(RemoteBook remoteBook)
        {
            return remoteBook?.PackDetection?.Verdict == ReleasePackDetectionVerdict.SingleBookSplitRelease
                ? remoteBook.PackDetection.SplitPart
                : null;
        }

        /// <summary>
        /// The split part of a tracked download, from what was known when it was grabbed: pack
        /// detection if the grab ran it, otherwise the grabbed release title. The files inside a
        /// part rarely name it ("001_ACOTAR0101P01.mp3"), so this outranks the files themselves.
        /// </summary>
        public static ReleaseSplitPart GetSplitPartOfGrab(RemoteBook remoteBook, string grabbedTitle)
        {
            return GetSplitPart(remoteBook) ?? ReleasePackDetector.DetectSplitPart(grabbedTitle);
        }

        /// <summary>
        /// Marks files imported from a tracked download with the split part it was grabbed as.
        /// Leaves them alone when the grab was a whole-book release.
        /// </summary>
        public static void ApplySplitPartOfGrab(IEnumerable<LocalBook> localBooks, RemoteBook remoteBook, string grabbedTitle)
        {
            var splitPart = GetSplitPartOfGrab(remoteBook, grabbedTitle);

            if (splitPart == null)
            {
                return;
            }

            foreach (var localBook in localBooks.Where(l => l != null))
            {
                localBook.ReleasePart = splitPart.Part;
                localBook.ReleasePartCount = splitPart.Count;
            }
        }

        /// <summary>
        /// Files on disk that a release would actually compete with: everything except the other
        /// parts of the same split. Part 1 on disk says nothing about whether Part 2 is wanted.
        /// </summary>
        public static IEnumerable<BookFile> ExcludeSiblingParts(IEnumerable<BookFile> existing, RemoteBook remoteBook)
        {
            var incomingPart = GetSplitPart(remoteBook);

            return incomingPart == null
                ? existing
                : existing.Where(f => !incomingPart.IsSiblingOf(GetSplitPart(f)));
        }

        public static List<BookFile> ExcludeSiblingParts(IEnumerable<BookFile> existing, IEnumerable<LocalBook> incoming)
        {
            var incomingPart = incoming?.Select(GetSplitPart).FirstOrDefault(p => p != null);

            return incomingPart == null
                ? existing.ToList()
                : existing.Where(f => !incomingPart.IsSiblingOf(GetSplitPart(f))).ToList();
        }
    }
}
