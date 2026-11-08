using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras
{
    public static class ExtraFilePathHelper
    {
        public static string ResolveOwnedSidecarPath(Author author, ExtraFile extra,
            IEnumerable<BookFile> files, IDiskProvider diskProvider)
        {
            if (extra.RelativePath.IsNullOrWhiteSpace() || Path.IsPathRooted(extra.RelativePath))
            {
                return null;
            }

            var fileFolders = files.Where(file => file != null && file.Path.IsNotNullOrWhiteSpace() &&
                    (!extra.BookFileId.HasValue || extra.BookFileId == file.Id) &&
                    (!extra.BookId.HasValue || extra.BookId == file.Edition?.BookId))
                .Select(file => Path.GetDirectoryName(file.Path)).ToList();
            var candidates = GetAuthorBasePaths(author)
                .Select(basePath => Path.GetFullPath(Path.Combine(basePath, extra.RelativePath)))
                .Where(path => fileFolders.Any(folder => folder.PathEquals(Path.GetDirectoryName(path))))
                .Distinct(NzbDrone.Common.PathEqualityComparer.Instance)
                .Where(diskProvider.FileExistsCanonical).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        public static void CleanupEmptySourceFolders(IDiskProvider diskProvider, Logger logger,
            IEnumerable<(string SourceFolder, string SourceAuthorFolderPath)> candidates)
        {
            foreach (var candidate in candidates)
            {
                var folder = candidate.SourceFolder;
                var boundary = candidate.SourceAuthorFolderPath;
                if (folder.IsNullOrWhiteSpace() || boundary.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    folder = Path.GetFullPath(folder);
                    boundary = Path.GetFullPath(boundary);
                    while (folder.PathEquals(boundary) || boundary.IsParentPath(folder))
                    {
                        if (diskProvider.FolderExists(folder))
                        {
                            if (diskProvider.GetFileAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                            {
                                break;
                            }

                            diskProvider.RemoveEmptySubfolders(folder);
                            if (diskProvider.GetFiles(folder, true).Any())
                            {
                                break;
                            }

                            // The final operation must refuse concurrent arrivals; an earlier
                            // empty check never authorizes recursively deleting the contents.
                            try
                            {
                                diskProvider.DeleteFolder(folder, recursive: false);
                                logger.Debug("Removed empty source folder: {0}", folder);
                            }
                            catch (IOException)
                            {
                                // Remaining files, sidecars or concurrent writes keep the folder.
                                logger.Debug("Keeping source folder because it is no longer empty or could not be removed: {0}", folder);
                            }
                        }

                        if (folder.PathEquals(boundary))
                        {
                            break;
                        }

                        folder = Path.GetDirectoryName(folder);
                        if (folder.IsNullOrWhiteSpace())
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Unable to clean empty source folders for {0}", candidate.SourceFolder);
                }
            }
        }

        public static List<string> GetAuthorBasePaths(Author author)
        {
            var paths = new List<string>();

            if (author == null)
            {
                return paths;
            }

            AddDistinct(paths, author.AudiobookPath);
            AddDistinct(paths, author.EbookPath);
            AddDistinct(paths, author.Path);

            return paths;
        }

        public static string GetPreferredBasePath(Author author, BookFile bookFile)
        {
            if (author == null)
            {
                return null;
            }

            var bookFilePath = bookFile?.Path;
            if (bookFilePath.IsNotNullOrWhiteSpace())
            {
                var bestMatch = GetAuthorBasePaths(author)
                    .Where(p => p.IsParentPath(bookFilePath) || p.PathEquals(bookFilePath))
                    .OrderByDescending(p => p.Length)
                    .FirstOrDefault();

                if (bestMatch.IsNotNullOrWhiteSpace())
                {
                    return bestMatch;
                }
            }

            var mediaType = bookFile?.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            if (mediaType.IsNotNullOrWhiteSpace())
            {
                var isAudiobook = !string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase);
                var perTypePath = author.GetPathForMediaType(isAudiobook);
                return perTypePath.IsNotNullOrWhiteSpace() ? perTypePath : author.Path;
            }

            return author.Path;
        }

        public static bool TryGetRelativePath(Author author, string fullPath, out string relativePath, out string basePath, string preferredBasePath = null)
        {
            relativePath = null;
            basePath = null;

            if (author == null || fullPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var candidates = GetRelativeBaseCandidates(author, fullPath, preferredBasePath);
            if (candidates.Count == 0)
            {
                return false;
            }

            basePath = candidates[0];

            if (!basePath.IsParentPath(fullPath) && !basePath.PathEquals(fullPath))
            {
                return false;
            }

            relativePath = NormalizePathSeparators(basePath.GetRelativePath(fullPath));
            return true;
        }

        public static string NormalizePathSeparators(string path)
        {
            return path?.Replace('\\', '/');
        }

        public static string ResolveFullPath(Author author, string relativePath, Func<string, bool> fileExists, string preferredBasePath = null)
        {
            if (author == null || relativePath.IsNullOrWhiteSpace())
            {
                return null;
            }

            var bases = GetAuthorBasePaths(author);
            PreferBase(bases, preferredBasePath);

            foreach (var basePath in bases)
            {
                if (basePath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var candidate = Path.Combine(basePath, relativePath);

                if (fileExists == null || fileExists(candidate))
                {
                    return candidate;
                }
            }

            var fallback = preferredBasePath.IsNotNullOrWhiteSpace()
                ? preferredBasePath
                : (bases.FirstOrDefault() ?? author.Path);

            return fallback.IsNullOrWhiteSpace() ? relativePath : Path.Combine(fallback, relativePath);
        }

        private static List<string> GetRelativeBaseCandidates(Author author, string fullPath, string preferredBasePath)
        {
            var bases = GetAuthorBasePaths(author);
            PreferBase(bases, preferredBasePath);

            var bestMatches = bases
                .Where(p => p.IsParentPath(fullPath) || p.PathEquals(fullPath))
                .OrderByDescending(p => p.Length)
                .ToList();

            if (bestMatches.Count > 0)
            {
                return bestMatches;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (directory.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            return bases
                .Where(p => p.IsParentPath(directory) || p.PathEquals(directory))
                .OrderByDescending(p => p.Length)
                .ToList();
        }

        private static void PreferBase(List<string> bases, string preferredBasePath)
        {
            if (preferredBasePath.IsNullOrWhiteSpace())
            {
                return;
            }

            var existing = bases.FindIndex(p => p.PathEquals(preferredBasePath));
            if (existing == 0)
            {
                return;
            }

            if (existing > 0)
            {
                bases.RemoveAt(existing);
                bases.Insert(0, preferredBasePath);
                return;
            }

            bases.Insert(0, preferredBasePath);
        }

        private static void AddDistinct(List<string> paths, string candidate)
        {
            if (candidate.IsNullOrWhiteSpace())
            {
                return;
            }

            if (paths.Any(p => p.PathEquals(candidate)))
            {
                return;
            }

            paths.Add(candidate);
        }
    }
}
