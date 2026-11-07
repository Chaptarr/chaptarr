using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books
{
    public interface IAuthorMediaMoveService
    {
        AuthorMediaMovePreview Preview(int authorId, string format, string destinationPath);
        AuthorMediaMoveBatchPreview PreviewBatch(IEnumerable<int> authorIds, string format, string destinationRootPath);
    }

    public class AuthorMediaMoveService : IAuthorMediaMoveService, IExecute<MoveAuthorMediaCommand>, IExecute<MoveAuthorMediaBatchCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataFileService _metadataFileService;
        private readonly IOtherExtraFileService _otherExtraFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly Logger _logger;

        public AuthorMediaMoveService(IAuthorService authorService,
                                      IMediaFileService mediaFileService,
                                      IMetadataFileService metadataFileService,
                                      IOtherExtraFileService otherExtraFileService,
                                      IDiskProvider diskProvider,
                                      IDiskTransferService diskTransferService,
                                      IRootFolderWatchingService rootFolderWatchingService,
                                      Logger logger)
        {
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _metadataFileService = metadataFileService;
            _otherExtraFileService = otherExtraFileService;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _rootFolderWatchingService = rootFolderWatchingService;
            _logger = logger;
        }

        public AuthorMediaMovePreview Preview(int authorId, string format, string destinationPath)
        {
            var author = _authorService.GetAuthor(authorId);
            var normalizedFormat = NormalizeFormat(format);
            var sourcePath = GetFormatPath(author, normalizedFormat);
            var fileExtensions = GetExtensions(normalizedFormat);
            var files = _mediaFileService.GetFilesByAuthor(authorId)
                .Where(file => fileExtensions.Contains(Path.GetExtension(file.Path)))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var metadataFiles = _metadataFileService.GetFilesByAuthor(authorId)
                .Where(file => file.BookFileId.HasValue)
                .ToLookup(file => file.BookFileId.Value);
            var otherExtraFiles = _otherExtraFileService.GetFilesByAuthor(authorId)
                .Where(file => file.BookFileId.HasValue)
                .ToLookup(file => file.BookFileId.Value);

            var preview = new AuthorMediaMovePreview
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                Format = normalizedFormat,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                MediaFileCount = files.Count
            };

            foreach (var bookFile in files)
            {
                var destination = GetDestinationPath(author, bookFile.Path, sourcePath, normalizedFormat, destinationPath, preview.Warnings);
                var sourceExists = _diskProvider.FileExists(bookFile.Path);
                AddFile(preview, new AuthorMediaMoveFile
                {
                    FileType = "media",
                    BookFileId = bookFile.Id,
                    SourcePath = bookFile.Path,
                    DestinationPath = destination,
                    SourceExists = sourceExists,
                    Size = sourceExists ? _diskProvider.GetFileSize(bookFile.Path) : bookFile.Size,
                    Modified = sourceExists ? _diskProvider.FileGetLastWrite(bookFile.Path) : bookFile.Modified
                });

                foreach (var metadataFile in metadataFiles[bookFile.Id])
                {
                    AddExtraFile(preview, author, bookFile, metadataFile.Id, "metadata", metadataFile.RelativePath, destination);
                }

                foreach (var extraFile in otherExtraFiles[bookFile.Id])
                {
                    AddExtraFile(preview, author, bookFile, extraFile.Id, "extra", extraFile.RelativePath, destination);
                }
            }

            if (files.Count == 0)
            {
                preview.Warnings.Add($"No {normalizedFormat} files are registered for this author.");
            }

            if (preview.MissingFileCount > 0)
            {
                preview.Warnings.Add($"{preview.MissingFileCount} registered media or sidecar files are missing and will be skipped.");
            }

            if (preview.AlreadyAtDestinationCount > 0)
            {
                preview.Warnings.Add($"{preview.AlreadyAtDestinationCount} files are already at their destination; their library records will be reconciled.");
            }

            if (ContainsFileInPath(destinationPath))
            {
                preview.Conflicts.Add($"The destination path overlaps an existing file: {destinationPath}");
            }

            AddConflictChecks(preview);
            AddSpaceInformation(preview);

            preview.TotalSize = preview.Files
                .Where(file => file.SourceExists || file.Status == "alreadyAtDestination")
                .Sum(file => file.Size);
            preview.PreviewToken = CreatePreviewToken(preview);
            preview.CanMove = preview.MediaFileCount > 0 && preview.Files.Any(file => file.SourceExists || file.Status == "alreadyAtDestination") &&
                preview.Conflicts.Count == 0 &&
                (!preview.AvailableSpace.HasValue || preview.RequiredCopyBytes <= preview.AvailableSpace.Value);

            return preview;
        }

        public AuthorMediaMoveBatchPreview PreviewBatch(IEnumerable<int> authorIds, string format, string destinationRootPath)
        {
            var normalizedFormat = NormalizeFormat(format);
            var authors = authorIds.Distinct().OrderBy(id => id)
                .Select(id =>
                {
                    var author = _authorService.GetAuthor(id);
                    var folderName = Path.GetFileName(author.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        throw new ArgumentException($"Unable to determine the library folder for {author.Name}.", nameof(authorIds));
                    }

                    return Preview(author.Id, normalizedFormat, Path.Combine(destinationRootPath, folderName));
                })
                .ToList();

            var preview = new AuthorMediaMoveBatchPreview
            {
                Format = normalizedFormat,
                DestinationRootPath = destinationRootPath,
                Authors = authors,
                RequiredCopyBytes = authors.Sum(author => author.RequiredCopyBytes),
                AvailableSpace = authors.Where(author => author.AvailableSpace.HasValue)
                    .Select(author => author.AvailableSpace)
                    .FirstOrDefault()
            };

            preview.Warnings = authors.SelectMany(author => author.Warnings).Distinct().ToList();
            preview.Conflicts = authors.SelectMany(author => author.Conflicts).Distinct().ToList();

            var duplicateTargets = authors.SelectMany(author => author.Files)
                .Where(file => file.Status != "missing")
                .GroupBy(file => file.DestinationPath, PathEqualityComparer.Instance)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateTargets)
            {
                preview.Conflicts.Add($"More than one file maps to the same destination: {duplicate.Key}");
            }

            if (preview.AvailableSpace.HasValue && preview.RequiredCopyBytes > preview.AvailableSpace.Value)
            {
                preview.Conflicts.Add("The destination does not have enough free space for the files that need to be copied.");
            }

            preview.PreviewToken = CreateBatchPreviewToken(preview);
            preview.CanMove = preview.Conflicts.Count == 0 && authors.Any(author => author.CanMove);

            return preview;
        }

        private void AddExtraFile(AuthorMediaMovePreview preview, Author author, BookFile bookFile, int extraFileId, string type, string relativePath, string destinationMediaPath)
        {
            var sourcePath = ResolveSidecarPath(author, bookFile, relativePath);
            preview.SidecarFileCount++;

            if (sourcePath == null)
            {
                preview.Conflicts.Add("A registered book sidecar has an invalid path outside the author's library folders.");
                return;
            }

            var destinationPath = Path.Combine(Path.GetDirectoryName(destinationMediaPath), Path.GetFileName(sourcePath));
            var sourceExists = _diskProvider.FileExists(sourcePath);

            AddFile(preview, new AuthorMediaMoveFile
            {
                FileType = type,
                BookFileId = bookFile.Id,
                ExtraFileId = extraFileId,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                SourceExists = sourceExists,
                Size = sourceExists ? _diskProvider.GetFileSize(sourcePath) : 0,
                Modified = sourceExists ? _diskProvider.FileGetLastWrite(sourcePath) : DateTime.MinValue
            });
        }

        private string ResolveSidecarPath(Author author, BookFile bookFile, string relativePath)
        {
            var normalizedRelativePath = (relativePath ?? string.Empty).Replace('\\', '/');

            if (string.IsNullOrWhiteSpace(normalizedRelativePath) ||
                Path.IsPathRooted(normalizedRelativePath) ||
                normalizedRelativePath.StartsWith("/", StringComparison.Ordinal) ||
                (normalizedRelativePath.Length >= 2 && char.IsLetter(normalizedRelativePath[0]) && normalizedRelativePath[1] == ':') ||
                normalizedRelativePath.Split('/').Any(segment => segment == ".."))
            {
                return null;
            }

            var localRelativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var preferredBasePath = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
            var basePaths = new[] { preferredBasePath, author.Path }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathEqualityComparer.Instance);

            foreach (var basePath in basePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(basePath, localRelativePath));
                if (!IsWithinPath(basePath, candidate))
                {
                    return null;
                }

                if (_diskProvider.FileExists(candidate))
                {
                    return candidate;
                }
            }

            var fallbackBasePath = preferredBasePath.IsNotNullOrWhiteSpace() ? preferredBasePath : author.Path;
            return fallbackBasePath.IsNullOrWhiteSpace()
                ? null
                : Path.GetFullPath(Path.Combine(fallbackBasePath, localRelativePath));
        }

        private static bool IsWithinPath(string basePath, string candidatePath)
        {
            var fullBasePath = Path.GetFullPath(basePath);
            var fullCandidatePath = Path.GetFullPath(candidatePath);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (fullBasePath.Equals(fullCandidatePath, comparison))
            {
                return true;
            }

            var baseWithSeparator = fullBasePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBasePath
                : fullBasePath + Path.DirectorySeparatorChar;

            return fullCandidatePath.StartsWith(baseWithSeparator, comparison);
        }

        private void AddFile(AuthorMediaMovePreview preview, AuthorMediaMoveFile file)
        {
            file.DestinationExists = _diskProvider.FileExists(file.DestinationPath);

            if (file.SourcePath.PathEquals(file.DestinationPath))
            {
                file.Status = "alreadyAtDestination";
            }
            else if (file.SourceExists)
            {
                file.Status = "ready";
            }
            else if (file.DestinationExists && file.FileType == "media" && IsExpectedFile(file))
            {
                file.Status = "alreadyAtDestination";
            }
            else if (file.DestinationExists)
            {
                file.Status = "conflict";
                preview.Conflicts.Add($"The source is missing and the destination already exists: {file.DestinationPath}");
            }
            else
            {
                file.Status = "missing";
            }

            preview.Files.Add(file);
        }

        private void AddConflictChecks(AuthorMediaMovePreview preview)
        {
            foreach (var file in preview.Files)
            {
                if (file.Status == "conflict")
                {
                    continue;
                }

                if (file.DestinationExists && !file.SourcePath.PathEquals(file.DestinationPath) && file.SourceExists)
                {
                    preview.Conflicts.Add($"Destination already exists: {file.DestinationPath}");
                }
            }

            var duplicateTargets = preview.Files
                .Where(file => file.Status != "missing")
                .GroupBy(file => file.DestinationPath, PathEqualityComparer.Instance)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateTargets)
            {
                preview.Conflicts.Add($"More than one file maps to the same destination: {duplicate.Key}");
            }
        }

        private void AddSpaceInformation(AuthorMediaMovePreview preview)
        {
            var targetMount = _diskProvider.GetMount(GetNearestExistingFolder(preview.DestinationPath));
            long requiredCopyBytes = 0;

            foreach (var file in preview.Files.Where(file => file.SourceExists && !file.SourcePath.PathEquals(file.DestinationPath)))
            {
                var sourceMount = _diskProvider.GetMount(file.SourcePath);
                if (sourceMount == null || targetMount == null || sourceMount.RootDirectory != targetMount.RootDirectory)
                {
                    requiredCopyBytes += file.Size;
                }
            }

            preview.RequiredCopyBytes = requiredCopyBytes;

            try
            {
                preview.AvailableSpace = _diskProvider.GetAvailableSpace(GetNearestExistingFolder(preview.DestinationPath));
            }
            catch (Exception ex)
            {
                var safeDestinationPath = (preview.DestinationPath ?? string.Empty)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                _logger.Debug(ex, "Unable to determine available space for media move target {0}", safeDestinationPath);
                preview.Warnings.Add("Available space at the destination could not be checked.");
            }

            if (preview.AvailableSpace.HasValue && preview.RequiredCopyBytes > preview.AvailableSpace.Value)
            {
                preview.Conflicts.Add("The destination does not have enough free space for the files that need to be copied.");
            }
        }

        private string GetNearestExistingFolder(string path)
        {
            var current = path;

            while (!string.IsNullOrWhiteSpace(current) && !_diskProvider.FolderExists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            return current ?? path;
        }

        private bool ContainsFileInPath(string path)
        {
            var current = path;

            while (!string.IsNullOrWhiteSpace(current))
            {
                if (_diskProvider.FileExists(current))
                {
                    return true;
                }

                if (_diskProvider.FolderExists(current))
                {
                    return false;
                }

                current = Path.GetDirectoryName(current);
            }

            return false;
        }

        private string CreatePreviewToken(AuthorMediaMovePreview preview)
        {
            var manifest = new StringBuilder();
            AppendTokenValue(manifest, preview.AuthorId.ToString(CultureInfo.InvariantCulture));
            AppendTokenValue(manifest, preview.Format);
            AppendTokenValue(manifest, preview.SourcePath);
            AppendTokenValue(manifest, preview.DestinationPath);

            foreach (var file in preview.Files
                         .OrderBy(file => file.SourcePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(file => file.FileType, StringComparer.Ordinal)
                         .ThenBy(file => file.BookFileId)
                         .ThenBy(file => file.ExtraFileId))
            {
                AppendTokenValue(manifest, file.FileType);
                AppendTokenValue(manifest, file.BookFileId.ToString(CultureInfo.InvariantCulture));
                AppendTokenValue(manifest, file.ExtraFileId.ToString(CultureInfo.InvariantCulture));
                AppendTokenValue(manifest, file.SourcePath);
                AppendTokenValue(manifest, file.DestinationPath);
                AppendTokenValue(manifest, file.SourceExists.ToString(CultureInfo.InvariantCulture));
                AppendTokenValue(manifest, file.Size.ToString(CultureInfo.InvariantCulture));
                AppendTokenValue(manifest, file.Modified.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private string CreateBatchPreviewToken(AuthorMediaMoveBatchPreview preview)
        {
            var manifest = new StringBuilder();
            AppendTokenValue(manifest, preview.Format);
            AppendTokenValue(manifest, preview.DestinationRootPath);

            foreach (var author in preview.Authors.OrderBy(author => author.AuthorId))
            {
                AppendTokenValue(manifest, author.PreviewToken);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void AppendTokenValue(StringBuilder manifest, string value)
        {
            var normalized = value ?? string.Empty;
            manifest.Append(normalized.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(normalized);
        }

        private string GetDestinationPath(Author author, string sourceFile, string sourcePath, string format, string destinationPath, List<string> warnings)
        {
            var formatPath = GetFormatPath(author, format);
            var candidates = new[] { sourcePath, formatPath, author.Path, author.EbookPath, author.AudiobookPath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathEqualityComparer.Instance);

            foreach (var candidate in candidates)
            {
                if (candidate.IsParentPath(sourceFile))
                {
                    return Path.Combine(destinationPath, candidate.GetRelativePath(sourceFile));
                }
            }

            const string warning = "Some media files are outside the author's configured folders; those files will keep their filenames without their parent folders.";
            if (!warnings.Contains(warning))
            {
                warnings.Add(warning);
            }

            return Path.Combine(destinationPath, Path.GetFileName(sourceFile));
        }

        private string GetFormatPath(Author author, string format)
        {
            if (format == "ebook" && author.EbookPath.IsNotNullOrWhiteSpace())
            {
                return author.EbookPath;
            }

            if (format == "audiobook" && author.AudiobookPath.IsNotNullOrWhiteSpace())
            {
                return author.AudiobookPath;
            }

            return author.Path;
        }

        private IReadOnlySet<string> GetExtensions(string format)
        {
            return format == "ebook" ? MediaFileExtensions.TextExtensions : MediaFileExtensions.AudioExtensions;
        }

        private string NormalizeFormat(string format)
        {
            if (string.Equals(format, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return "ebook";
            }

            if (string.Equals(format, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                return "audiobook";
            }

            throw new ArgumentException("Format must be 'ebook' or 'audiobook'.", nameof(format));
        }

        public void Execute(MoveAuthorMediaCommand message)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            var files = message.Files ?? new List<AuthorMediaMoveFile>();

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                _logger.ProgressInfo("Moving {0} {1} files ({2}/{3})", author.Name, message.Format, index + 1, files.Count);
                MoveFile(author, file, message.Format);
            }

            _logger.ProgressInfo("Finished moving {0} {1} files to {2}", author.Name, message.Format, message.DestinationPath);
        }

        public void Execute(MoveAuthorMediaBatchCommand message)
        {
            var authors = message.Authors ?? new List<AuthorMediaMoveBatchItem>();
            var totalFiles = authors.Sum(author => author.Files?.Count ?? 0);
            var processedFiles = 0;

            foreach (var authorMove in authors)
            {
                var author = _authorService.GetAuthor(authorMove.AuthorId);
                var files = authorMove.Files ?? new List<AuthorMediaMoveFile>();

                foreach (var file in files)
                {
                    processedFiles++;
                    _logger.ProgressInfo("Moving {0} {1} files ({2}/{3})", author.Name, message.Format, processedFiles, totalFiles);
                    MoveFile(author, file, message.Format);
                }

                _logger.ProgressInfo("Finished moving {0} {1} files to {2}", author.Name, message.Format, authorMove.DestinationPath);
            }

            _logger.ProgressInfo("Finished bulk move of {0} {1} files to {2}", totalFiles, message.Format, message.DestinationRootPath);
        }

        private void MoveFile(Author author, AuthorMediaMoveFile file, string format)
        {
            if (file.Status == "missing")
            {
                _logger.Warn("Skipping missing source file {0}", file.SourcePath);
                return;
            }

            var sourceExists = _diskProvider.FileExists(file.SourcePath);
            var destinationExists = _diskProvider.FileExists(file.DestinationPath);

            if (file.SourcePath.PathNotEquals(file.DestinationPath))
            {
                if (sourceExists && destinationExists)
                {
                    throw new IOException($"Move stopped because the destination already exists: {file.DestinationPath}");
                }

                if (sourceExists)
                {
                    if (_diskProvider.GetFileSize(file.SourcePath) != file.Size || _diskProvider.FileGetLastWrite(file.SourcePath) != file.Modified)
                    {
                        throw new IOException($"Move stopped because the source changed after preview: {file.SourcePath}");
                    }

                    _diskProvider.EnsureFolder(Path.GetDirectoryName(file.DestinationPath));
                    _rootFolderWatchingService.ReportFileSystemChangeBeginning(file.SourcePath, file.DestinationPath);
                    _diskTransferService.TransferFile(file.SourcePath, file.DestinationPath, TransferMode.Move);
                }
                else if (destinationExists && IsExpectedFile(file))
                {
                    _logger.Debug("Reconciling already moved file {0} to {1}", file.SourcePath, file.DestinationPath);
                }
                else if (destinationExists)
                {
                    throw new IOException($"Move stopped because the destination does not match the expected file: {file.DestinationPath}");
                }
                else
                {
                    _logger.Warn("Skipping missing source file {0}", file.SourcePath);
                    return;
                }
            }

            UpdateFileRecord(author, file, format);
        }

        private bool IsExpectedFile(AuthorMediaMoveFile file)
        {
            if (_diskProvider.GetFileSize(file.DestinationPath) != file.Size)
            {
                return false;
            }

            return _diskProvider.FileGetLastWrite(file.DestinationPath) == file.Modified;
        }

        private void UpdateFileRecord(Author author, AuthorMediaMoveFile file, string format)
        {
            if (file.FileType == "media")
            {
                var bookFile = _mediaFileService.Get(file.BookFileId);
                bookFile.Path = file.DestinationPath;
                _mediaFileService.Update(bookFile);
                return;
            }

            if (file.FileType == "metadata")
            {
                var metadataFile = _metadataFileService.GetFilesByBookFile(file.BookFileId).FirstOrDefault(extra => extra.Id == file.ExtraFileId);
                if (metadataFile != null)
                {
                    metadataFile.RelativePath = ExtraFilePathHelper.NormalizePathSeparators(GetFormatPath(author, NormalizeFormat(format)).GetRelativePath(file.DestinationPath));
                    _metadataFileService.Upsert(metadataFile);
                }

                return;
            }

            var otherExtraFile = _otherExtraFileService.GetFilesByBookFile(file.BookFileId).FirstOrDefault(extra => extra.Id == file.ExtraFileId);
            if (otherExtraFile != null)
            {
                otherExtraFile.RelativePath = ExtraFilePathHelper.NormalizePathSeparators(GetFormatPath(author, NormalizeFormat(format)).GetRelativePath(file.DestinationPath));
                _otherExtraFileService.Upsert(otherExtraFile);
            }
        }
    }
}
