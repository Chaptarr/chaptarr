using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class CanonicalMatchInputBuilder
    {
        internal static string BuildEmbeddedQuery(IDictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return string.Empty;
            }

            var parts = tags
                .Where(kv => kv.Value != null && !TagExclusionPolicy.IsExcludedFromMatching(kv.Key))
                .SelectMany(kv => kv.Value)
                .Select(v => v?.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length < 500)
                .Select(v => v.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return string.Join(" ", parts);
        }

        internal static Dictionary<string, List<string>> BuildPathDerivedTags(string filePath, string bookFolderPath = null, string authorFolderPath = null)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return tags;
            }

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
                var dir = Path.GetDirectoryName(filePath);

                if (string.IsNullOrWhiteSpace(bookFolderPath) && !string.IsNullOrWhiteSpace(dir))
                {
                    bookFolderPath = dir;
                }

                if (string.IsNullOrWhiteSpace(authorFolderPath) && !string.IsNullOrWhiteSpace(dir))
                {
                    authorFolderPath = Directory.GetParent(dir)?.FullName;
                }

                var bookFolder = ResolveFolderName(bookFolderPath);
                var authorFolder = ResolveFolderName(authorFolderPath);

                if (!string.IsNullOrWhiteSpace(bookFolder))
                {
                    tags["ALBUM"] = new List<string> { bookFolder };
                }

                if (!string.IsNullOrWhiteSpace(authorFolder))
                {
                    tags["ARTIST"] = new List<string> { authorFolder };
                    tags["ALBUMARTIST"] = new List<string> { authorFolder };
                    tags["AUTHOR"] = new List<string> { authorFolder };
                }

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    tags["TITLE"] = new List<string> { fileName };
                }
            }
            catch
            {
                // best-effort only
            }

            return tags;
        }

        private static string ResolveFolderName(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            try
            {
                return new DirectoryInfo(folderPath).Name;
            }
            catch
            {
                return folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }
}
