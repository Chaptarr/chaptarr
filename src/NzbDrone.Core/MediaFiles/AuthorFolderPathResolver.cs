using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.MediaFiles
{
    public interface IAuthorFolderPathResolver
    {
        string GetAuthorPath(string rootFolderPath, Author author, string mediaType);
    }

    public class AuthorFolderPathResolver : IAuthorFolderPathResolver
    {
        private readonly IBuildFileNames _buildFileNames;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public AuthorFolderPathResolver(IBuildFileNames buildFileNames, IDiskProvider diskProvider, Logger logger)
        {
            _buildFileNames = buildFileNames;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public string GetAuthorPath(string rootFolderPath, Author author, string mediaType)
        {
            rootFolderPath = NormalizeFolderPath(rootFolderPath);
            var proposedAuthorFolder = _buildFileNames.GetAuthorFolder(author, mediaType: mediaType);

            var rootFolderName = Path.GetFileName(rootFolderPath);
            var normalizedRootFolderName = Books.Extensions.StringSuperNormalizer.SuperNormalize(rootFolderName);
            var normalizedAuthorName = Books.Extensions.StringSuperNormalizer.SuperNormalize(author.Name);
            var normalizedProposedFolder = Books.Extensions.StringSuperNormalizer.SuperNormalize(proposedAuthorFolder);

            if (normalizedRootFolderName == normalizedAuthorName ||
                normalizedRootFolderName == normalizedProposedFolder)
            {
                _logger.Debug("Author path already contains author folder: {0}", rootFolderPath);
                return rootFolderPath;
            }

            var parentPath = Path.GetDirectoryName(rootFolderPath);
            var existingAuthorFolder = FindExistingAuthorFolder(rootFolderPath, proposedAuthorFolder, author.Name);

            if (!existingAuthorFolder.IsNullOrWhiteSpace())
            {
                _logger.Debug("Found existing author folder with different formatting: {0} instead of creating {1}",
                    existingAuthorFolder,
                    Path.Combine(rootFolderPath, proposedAuthorFolder));

                return existingAuthorFolder;
            }

            if (parentPath != null && _diskProvider.FolderExists(rootFolderPath))
            {
                var existingFolderName = Path.GetFileName(rootFolderPath);
                var normalizedExisting = Books.Extensions.StringSuperNormalizer.SuperNormalize(existingFolderName);

                if (normalizedExisting == normalizedAuthorName || normalizedExisting == normalizedProposedFolder)
                {
                    _logger.Debug("Using existing author folder despite formatting differences: {0}", rootFolderPath);
                    return rootFolderPath;
                }
            }

            return Path.Combine(rootFolderPath, proposedAuthorFolder);
        }

        private string FindExistingAuthorFolder(string rootFolderPath, string proposedFolderName, string authorName)
        {
            try
            {
                if (rootFolderPath.IsNullOrWhiteSpace())
                {
                    return null;
                }

                if (!_diskProvider.FolderExists(rootFolderPath))
                {
                    return null;
                }

                var normalizedProposed = proposedFolderName.NormalizeAuthorNameForComparison();
                var normalizedAuthorName = authorName.NormalizeAuthorNameForComparison();
                var subdirectories = _diskProvider.GetDirectories(rootFolderPath);

                foreach (var dir in subdirectories)
                {
                    var folderName = Path.GetFileName(dir);
                    var normalizedFolder = folderName.NormalizeAuthorNameForComparison();

                    if (normalizedFolder == normalizedProposed || normalizedFolder == normalizedAuthorName)
                    {
                        _logger.Debug("Found existing author folder match: {0} (normalized: {1}) matches {2} or {3}",
                            folderName,
                            normalizedFolder,
                            normalizedProposed,
                            normalizedAuthorName);

                        return dir;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error checking for existing author folders in {0}", rootFolderPath);
                return null;
            }
        }

        private static string NormalizeFolderPath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return path;
            }

            var root = Path.GetPathRoot(path);
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.IsNullOrWhiteSpace())
            {
                return root ?? path;
            }

            if (root.IsNotNullOrWhiteSpace() &&
                trimmed.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return trimmed;
        }
    }
}
