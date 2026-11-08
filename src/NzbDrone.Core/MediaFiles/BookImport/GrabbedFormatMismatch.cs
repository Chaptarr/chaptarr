using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class GrabbedFormatMismatch
    {
        public const string RejectionCategory = "GrabbedFormatMismatch";

        public static bool IsBookFile(string path) =>
            QualityMediaTypeHelper.GetMediaTypeFromPath(path).HasValue && !DiskScanService.IsExcludedFile(path);

        public static string Message(string releaseTitle, IEnumerable<string> paths, BookMediaType? targetMedia,
            Quality expectedQuality = null, QualityProfile profile = null)
        {
            var files = paths.Where(IsBookFile).Distinct(PathEqualityComparer.Instance).ToList();
            var otherSide = targetMedia.HasValue && files.All(path => QualityMediaTypeHelper.GetMediaTypeFromPath(path) != targetMedia);
            var mediaLabel = targetMedia == BookMediaType.Audiobook ? "an audiobook" : targetMedia == BookMediaType.Ebook ? "an ebook" : "a book";
            var expected = mediaLabel;
            if (!otherSide)
            {
                var allowed = profile?.Items.Where(item => item.Allowed).SelectMany(item => item.GetQualities()).ToList();
                if (expectedQuality != null && expectedQuality != Quality.Unknown && expectedQuality != Quality.UnknownAudio)
                {
                    expected = expectedQuality.Name;
                }
                else if (allowed?.Count > 0)
                {
                    expected = string.Join(" or ", allowed.Select(quality => quality.Name).Distinct());
                }
            }

            var groups = files.GroupBy(path => Path.GetExtension(path).TrimStart('.').ToUpperInvariant()).ToList();
            var actual = otherSide
                ? targetMedia == BookMediaType.Audiobook ? "ebook" : "audiobook"
                : string.Join(" / ", groups.Select(group => group.Key));
            var examples = string.Join("; ", groups.Select(group =>
                $"{group.Key}: {string.Join(", ", group.Take(3).Select(Path.GetFileName))}" +
                (group.Count() > 3 ? $", and {group.Count() - 3} more" : string.Empty)));
            var mismatch = !otherSide && expectedQuality != null && files.Any(path =>
                MediaFileExtensions.GetQualityForExtension(Path.GetExtension(path)) == expectedQuality)
                ? $"but its {actual} files are not allowed by the quality profile"
                : $"but it only contains {actual} files";
            return $"Grabbed {releaseTitle} as {expected}, {mismatch} ({examples}). " +
                "Use Manual Import to import them anyway, or remove the download.";
        }
    }
}
