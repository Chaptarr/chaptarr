using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IBuildAuthorPaths
    {
        string BuildPath(Author author, bool useExistingRelativeFolder);
        string BuildPathForQuality(Author author, NzbDrone.Core.Qualities.Quality quality, bool useExistingRelativeFolder);
        void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder, Author previous = null);
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

        public static List<MoveAuthorCommand> ChangeFolders(Author author, string audiobookFolder, string ebookFolder,
            List<RootFolder> roots, bool moveFiles)
        {
            var audioChanged = audiobookFolder != null && !audiobookFolder.PathEquals(author.AudiobookPath);
            var ebookChanged = ebookFolder != null && !ebookFolder.PathEquals(author.EbookPath);
            var shared = author.AudiobookPath.IsNotNullOrWhiteSpace() && author.AudiobookPath.PathEquals(author.EbookPath);
            if (moveFiles && shared && (audioChanged || ebookChanged))
            {
                if (audioChanged && ebookChanged && !audiobookFolder.PathEquals(ebookFolder))
                {
                    throw new ValidationException("A shared author folder cannot be moved to two destinations. Choose one folder for both formats.");
                }

                audiobookFolder = ebookFolder = audioChanged ? audiobookFolder : ebookFolder;
                audioChanged = ebookChanged = true;
            }

            var audioSource = author.AudiobookPath.IsNotNullOrWhiteSpace() ? author.AudiobookPath :
                author.EbookRootFolderPath.IsNullOrWhiteSpace() && author.EbookPath.IsNullOrWhiteSpace() ? author.Path : null;
            var ebookSource = author.EbookPath.IsNotNullOrWhiteSpace() ? author.EbookPath :
                author.AudiobookRootFolderPath.IsNullOrWhiteSpace() && author.AudiobookPath.IsNullOrWhiteSpace() ? author.Path : null;
            var changes = new[]
            {
                (Changed: audioChanged, Folder: audiobookFolder, Source: audioSource, SourceRoot: author.AudiobookRootFolderPath, Media: "audiobook"),
                (Changed: ebookChanged, Folder: ebookFolder, Source: ebookSource, SourceRoot: author.EbookRootFolderPath, Media: "ebook")
            }.Where(change => change.Changed).Select(change =>
            {
                var field = change.Media == "ebook" ? "EbookFolder" : "AudiobookFolder";
                var path = change.Folder;
                RootFolder root = null;
                if (path.IsNotNullOrWhiteSpace() && Path.IsPathFullyQualified(path))
                {
                    path = Path.GetFullPath(path).GetCleanPath();
                    root = roots.Where(folder => folder.Path.IsParentPath(path))
                        .OrderByDescending(folder => folder.Path.Length).FirstOrDefault();
                }

                if (root == null || IsUnsafePath(path, roots) ||
                    (root.FolderType != FolderType.Mixed && root.FolderType != (change.Media == "ebook" ? FolderType.Ebook : FolderType.Audiobook)))
                {
                    throw new ValidationException(new[] { new ValidationFailure(field,
                        $"The {change.Media} folder must be inside a configured root folder that supports {change.Media}s.") });
                }

                if (moveFiles && change.Source.IsNotNullOrWhiteSpace() && change.Source.IsParentPath(path))
                {
                    throw new ValidationException(new[] { new ValidationFailure(field, "An author folder cannot be moved inside itself.") });
                }

                return (change.Media, change.Source, change.SourceRoot, Path: path, Root: root.Path);
            }).ToList();

            var moves = new List<MoveAuthorCommand>();
            foreach (var change in changes)
            {
                if (moveFiles && change.Source.IsNotNullOrWhiteSpace() && !change.Source.PathEquals(change.Path) &&
                    !moves.Any(move => move.SourcePath.PathEquals(change.Source) && move.DestinationPath.PathEquals(change.Path)))
                {
                    moves.Add(new MoveAuthorCommand
                    {
                        AuthorId = author.Id, SourcePath = change.Source, DestinationPath = change.Path,
                        MediaType = change.Media, SourceRootFolder = change.SourceRoot,
                        SharedFolder = shared && audioChanged && ebookChanged
                    });
                }

                if (change.Media == "audiobook")
                {
                    author.AudiobookPath = change.Path;
                    author.AudiobookRootFolderPath = change.Root;
                }
                else
                {
                    author.EbookPath = change.Path;
                    author.EbookRootFolderPath = change.Root;
                }
            }

            if (changes.Count > 0)
            {
                author.Path = author.AudiobookPath.IsNotNullOrWhiteSpace() ? author.AudiobookPath : author.EbookPath;
            }

            return moves;
        }

        public void EnsureAuthorPaths(Author author, bool useExistingRelativeFolder, Author previous = null)
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

            // An unrelated save must not relocate an existing tracked library in the database.
            // Initial setup and a deliberate root change still build the affected format's path.
            var updateAudio = hasAudiobookRoot && (previous == null || author.AudiobookPath.IsNullOrWhiteSpace() ||
                !audiobookRoot.PathEquals(previous.AudiobookRootFolderPath));
            var updateEbook = hasEbookRoot && (previous == null || author.EbookPath.IsNullOrWhiteSpace() ||
                !ebookRoot.PathEquals(previous.EbookRootFolderPath));

            if ((updateAudio || updateEbook) && hasAudiobookRoot && hasEbookRoot && audiobookRoot.PathEquals(ebookRoot) &&
                (author.AudiobookPath.IsNullOrWhiteSpace() || author.EbookPath.IsNullOrWhiteSpace() || author.AudiobookPath.PathEquals(author.EbookPath)))
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

                var sharedPath = !updateAudio ? author.AudiobookPath : !updateEbook ? author.EbookPath :
                    BuildPathUnderRoot(sharedRoot, existingPath, authorFolder, useExistingRelativeFolder, rootFolders);

                if (updateAudio) author.AudiobookPath = sharedPath;
                if (updateEbook) author.EbookPath = sharedPath;

                if (NeedsPathRepair(author.Path, sharedRoot, rootFolders))
                {
                    author.Path = sharedPath;
                }

                return;
            }

            if (updateAudio)
            {
                var audiobookFolder = _fileNameBuilder.GetAuthorFolder(author, mediaType: "audiobook");
                if (audiobookFolder.IsNotNullOrWhiteSpace() &&
                    NeedsPathRepair(author.AudiobookPath, audiobookRoot, rootFolders))
                {
                    var existingPath = author.AudiobookPath.IsNotNullOrWhiteSpace() ? author.AudiobookPath : author.Path;
                    author.AudiobookPath = BuildPathUnderRoot(audiobookRoot, existingPath, audiobookFolder, useExistingRelativeFolder, rootFolders);
                }
            }

            if (updateEbook)
            {
                var ebookFolder = _fileNameBuilder.GetAuthorFolder(author, mediaType: "ebook");
                if (ebookFolder.IsNotNullOrWhiteSpace() &&
                    NeedsPathRepair(author.EbookPath, ebookRoot, rootFolders))
                {
                    var existingPath = author.EbookPath.IsNotNullOrWhiteSpace()
                        ? author.EbookPath
                        : (author.AudiobookRootFolderPath.IsNullOrWhiteSpace() ? author.Path : null);

                    author.EbookPath = BuildPathUnderRoot(ebookRoot, existingPath, ebookFolder, useExistingRelativeFolder, rootFolders);
                }
            }

            var primaryRoot = hasAudiobookRoot ? audiobookRoot : ebookRoot;
            if (primaryRoot.IsNullOrWhiteSpace() || (previous != null && author.Path.IsNotNullOrWhiteSpace() &&
                !(hasAudiobookRoot ? updateAudio : updateEbook)))
            {
                return;
            }

            if (NeedsPathRepair(author.Path, primaryRoot, rootFolders))
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
                        author.Path = BuildPathUnderRoot(primaryRoot, author.Path, primaryFolder, useExistingRelativeFolder, rootFolders);
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


        private static bool NeedsPathRepair(string path, string root, System.Collections.Generic.List<RootFolder> rootFolders)
        {
            return path.IsNullOrWhiteSpace() ||
                   IsUnsafePath(path, rootFolders) ||
                   path.PathEquals(root) ||
                   !root.IsParentPath(path);
        }

        private string BuildPathUnderRoot(string root, string existingPath, string authorFolder, bool useExistingRelativeFolder, List<RootFolder> rootFolders)
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
                var rootFolderPath = _rootFolderService.GetBestRootFolderPath(existingPath, rootFolders);
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
