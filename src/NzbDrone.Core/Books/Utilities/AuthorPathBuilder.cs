using System;
using System.IO;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IBuildAuthorPaths
    {
        string BuildPath(Author author, bool useExistingRelativeFolder);
        string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder);
        void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder);
    }

    public class AuthorPathBuilder : IBuildAuthorPaths
    {
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IRootFolderService _rootFolderService;

        public AuthorPathBuilder(IBuildFileNames fileNameBuilder, IRootFolderService rootFolderService)
        {
            _fileNameBuilder = fileNameBuilder;
            _rootFolderService = rootFolderService;
        }

        public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder)
        {
            if (author == null)
            {
                throw new ArgumentNullException(nameof(author));
            }

            var rootFolders = _rootFolderService.All() ?? new System.Collections.Generic.List<RootFolder>();

            var audiobookRoot = author.AudiobookRootFolderPath;
            var ebookRoot = author.EbookRootFolderPath;

            var hasAudiobookRoot = audiobookRoot.IsNotNullOrWhiteSpace();
            var hasEbookRoot = ebookRoot.IsNotNullOrWhiteSpace();

            if (hasAudiobookRoot && hasEbookRoot && audiobookRoot.PathEquals(ebookRoot))
            {
                var sharedRoot = audiobookRoot;
                var authorFolder = _fileNameBuilder.GetAuthorFolder(author); // Mixed/shared root uses a single author folder format

                if (authorFolder.IsNullOrWhiteSpace())
                {
                    return;
                }

                var existingPath = author.AudiobookPath.IsNotNullOrWhiteSpace()
                    ? author.AudiobookPath
                    : (author.EbookPath.IsNotNullOrWhiteSpace() ? author.EbookPath : author.Path);

                var sharedPath = BuildPathUnderRoot(sharedRoot, existingPath, authorFolder, useExistingRelativeFolder);

                author.AudiobookPath = sharedPath;
                author.EbookPath = sharedPath;

                if (NeedsPrimaryPathRepair(author.Path, sharedRoot, rootFolders))
                {
                    author.Path = sharedPath;
                }

                return;
            }

            if (hasAudiobookRoot)
            {
                var audiobookFolder = _fileNameBuilder.GetAuthorFolder(author, mediaType: "audiobook");
                if (audiobookFolder.IsNotNullOrWhiteSpace() &&
                    NeedsPerTypePathRepair(author.AudiobookPath, audiobookRoot, rootFolders))
                {
                    var existingPath = author.AudiobookPath.IsNotNullOrWhiteSpace() ? author.AudiobookPath : author.Path;
                    author.AudiobookPath = BuildPathUnderRoot(audiobookRoot, existingPath, audiobookFolder, useExistingRelativeFolder);
                }
            }

            if (hasEbookRoot)
            {
                var ebookFolder = _fileNameBuilder.GetAuthorFolder(author, mediaType: "ebook");
                if (ebookFolder.IsNotNullOrWhiteSpace() &&
                    NeedsPerTypePathRepair(author.EbookPath, ebookRoot, rootFolders))
                {
                    var existingPath = author.EbookPath.IsNotNullOrWhiteSpace()
                        ? author.EbookPath
                        : (author.AudiobookRootFolderPath.IsNullOrWhiteSpace() ? author.Path : null);

                    author.EbookPath = BuildPathUnderRoot(ebookRoot, existingPath, ebookFolder, useExistingRelativeFolder);
                }
            }

            var primaryRoot = hasAudiobookRoot ? audiobookRoot : ebookRoot;
            if (primaryRoot.IsNullOrWhiteSpace())
            {
                return;
            }

            if (NeedsPrimaryPathRepair(author.Path, primaryRoot, rootFolders))
            {
                var desiredPrimary = hasAudiobookRoot ? author.AudiobookPath : author.EbookPath;
                if (desiredPrimary.IsNotNullOrWhiteSpace() && primaryRoot.IsParentPath(desiredPrimary))
                {
                    author.Path = desiredPrimary;
                }
                else
                {
                    var primaryFolder = hasAudiobookRoot
                        ? _fileNameBuilder.GetAuthorFolder(author, mediaType: "audiobook")
                        : _fileNameBuilder.GetAuthorFolder(author, mediaType: "ebook");

                    if (primaryFolder.IsNotNullOrWhiteSpace())
                    {
                        author.Path = BuildPathUnderRoot(primaryRoot, author.Path, primaryFolder, useExistingRelativeFolder);
                    }
                }
            }
        }

        public string BuildPath(Author author, bool useExistingRelativeFolder)
        {
            // Check if we have a discovered path for either media type
            if (!string.IsNullOrWhiteSpace(author.AudiobookPath) &&
                !(author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() && author.AudiobookPath.PathEquals(author.AudiobookRootFolderPath)))
            {
                return author.AudiobookPath;
            }

            if (!string.IsNullOrWhiteSpace(author.EbookPath) &&
                !(author.EbookRootFolderPath.IsNotNullOrWhiteSpace() && author.EbookPath.PathEquals(author.EbookRootFolderPath)))
            {
                return author.EbookPath;
            }

            // Fall back to existing logic
            var rootFolder = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath)
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("No root folder was provided", nameof(author));
            }

            if (useExistingRelativeFolder && author.Path.IsNotNullOrWhiteSpace())
            {
                var relativePath = GetExistingRelativePath(author);
                return Path.Combine(rootFolder, relativePath);
            }

            var mediaType = !string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath) ? "audiobook" : "ebook";
            return Path.Combine(rootFolder, _fileNameBuilder.GetAuthorFolder(author, mediaType: mediaType));
        }

        public string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder)
        {
            var rootFolder = author.GetRootFolderForQuality(quality);

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException($"No root folder configured for quality type: {quality.Name}", nameof(author));
            }

            // Check if it's an ebook or audiobook quality
            var isEbook = quality.Id == 1 || quality.Id == 2 || quality.Id == 3 || quality.Id == 4;

            // Use the discovered path for the specific media type if available
            if (isEbook && !string.IsNullOrWhiteSpace(author.EbookPath) &&
                !author.EbookPath.PathEquals(rootFolder) &&
                rootFolder.IsParentPath(author.EbookPath))
            {
                return author.EbookPath;
            }
            else if (!isEbook && !string.IsNullOrWhiteSpace(author.AudiobookPath) &&
                     !author.AudiobookPath.PathEquals(rootFolder) &&
                     rootFolder.IsParentPath(author.AudiobookPath))
            {
                return author.AudiobookPath;
            }

            // Fall back to existing logic if no discovered path
            if (useExistingRelativeFolder && author.Path.IsNotNullOrWhiteSpace())
            {
                var relativePath = GetExistingRelativePath(author);
                return Path.Combine(rootFolder, relativePath);
            }

            var mediaType = isEbook ? "ebook" : "audiobook";
            return Path.Combine(rootFolder, _fileNameBuilder.GetAuthorFolder(author, mediaType: mediaType));
        }

        private string GetExistingRelativePath(Author author)
        {
            var rootFolderPath = _rootFolderService.GetBestRootFolderPath(author.Path);

            return rootFolderPath.GetRelativePath(author.Path);
        }

        private static bool NeedsPrimaryPathRepair(string path, string root, System.Collections.Generic.List<RootFolder> rootFolders)
        {
            return path.IsNullOrWhiteSpace() ||
                   IsUnsafePath(path, rootFolders) ||
                   path.PathEquals(root) ||
                   !root.IsParentPath(path);
        }

        private static bool NeedsPerTypePathRepair(string path, string root, System.Collections.Generic.List<RootFolder> rootFolders)
        {
            return path.IsNullOrWhiteSpace() ||
                   IsUnsafePath(path, rootFolders) ||
                   path.PathEquals(root) ||
                   !root.IsParentPath(path);
        }

        private string BuildPathUnderRoot(string root, string existingPath, string authorFolder, bool useExistingRelativeFolder)
        {
            if (root.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("No root folder was provided", nameof(root));
            }

            if (authorFolder.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("No author folder was provided", nameof(authorFolder));
            }

            if (!useExistingRelativeFolder || existingPath.IsNullOrWhiteSpace())
            {
                return Path.Combine(root, authorFolder);
            }

            try
            {
                var rootFolderPath = _rootFolderService.GetBestRootFolderPath(existingPath);
                if (rootFolderPath.IsParentPath(existingPath))
                {
                    var relativePath = rootFolderPath.GetRelativePath(existingPath);
                    if (relativePath.IsNotNullOrWhiteSpace())
                    {
                        return Path.Combine(root, relativePath);
                    }
                }
            }
            catch
            {
                // Fall back to computed author folder if relative path cannot be determined.
            }

            return Path.Combine(root, authorFolder);
        }

        private static bool IsUnsafePath(string path, System.Collections.Generic.List<RootFolder> rootFolders)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return true;
            }

            if (rootFolders == null || rootFolders.Count == 0)
            {
                return false;
            }

            // Unsafe if it is a root folder itself, or if it is a parent of a root folder.
            foreach (var rootFolder in rootFolders)
            {
                if (rootFolder?.Path.IsNullOrWhiteSpace() == true)
                {
                    continue;
                }

                if (rootFolder.Path.PathEquals(path))
                {
                    return true;
                }

                if (path.IsParentPath(rootFolder.Path))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
