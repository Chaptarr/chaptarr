using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMoveBookFiles
    {
        BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null);
        BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null);
        BookFile MoveBookFile(BookFile bookFile, LocalBook localBook);
        BookFile CopyBookFile(BookFile bookFile, LocalBook localBook);
        string GetImportDestinationPath(BookFile bookFile, LocalBook localBook);
    }

    public sealed class BookFileMovePlan
    {
        public bool CanOrganize { get; set; }
        public string SkipReason { get; set; }
        public string SourceAuthorFolderPath { get; set; }
        public string DestinationAuthorFolderPath { get; set; }
        public string DestinationPath { get; set; }
        public List<string> ReplicaPaths { get; set; }
        public bool ShouldCleanupReplicas { get; set; }
        public bool ShouldUpdateStoredAuthorPath { get; set; }

        /// <summary>
        /// True when the destination was chosen by the ebook colocation planner (i.e. the file is being
        /// placed alongside an audiobook of the same work) rather than by the standard naming format.
        /// This is the single source of truth for "this plan is a colocation": it selects the colocating
        /// branch in <see cref="BookFileMovingService.MoveBookFile(BookFile, Author, BookFileMovePlan, RenameBatchContext)"/>,
        /// and <see cref="ReplicaPaths"/> is populated only when it is set. Callers whose only mandate is
        /// colocation must not move files when this is false.
        /// </summary>
        public bool ColocationApplied { get; set; }

        public static BookFileMovePlan Skipped(string reason)
        {
            return new BookFileMovePlan
            {
                CanOrganize = false,
                SkipReason = reason
            };
        }
    }

    public class BookFileMovingService : IMoveBookFiles
    {
        private readonly IEditionService _editionService;
        private readonly IUpdateBookFileService _updateBookFileService;
        private readonly IBuildFileNames _buildFileNames;
        private readonly IBuildAuthorPaths _authorPathBuilder;
        private readonly INamingConfigService _namingConfigService;
        private readonly IEbookColocationPlanner _ebookColocationPlanner;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;
        private readonly Logger _logger;

        public BookFileMovingService(IEditionService editionService,
                                      IUpdateBookFileService updateBookFileService,
                                      IBuildFileNames buildFileNames,
                                      IBuildAuthorPaths authorPathBuilder,
                                      INamingConfigService namingConfigService,
                                      IEbookColocationPlanner ebookColocationPlanner,
                                      IDiskTransferService diskTransferService,
                                      IDiskProvider diskProvider,
                                      IRecycleBinProvider recycleBinProvider,
                                      IRootFolderWatchingService rootFolderWatchingService,
                                      IMediaFileAttributeService mediaFileAttributeService,
                                      IEventAggregator eventAggregator,
                                      IConfigService configService,
                                      IFileMutationSafetyService fileMutationSafetyService,
                                      Logger logger)
        {
            _editionService = editionService;
            _updateBookFileService = updateBookFileService;
            _buildFileNames = buildFileNames;
            _authorPathBuilder = authorPathBuilder;
            _namingConfigService = namingConfigService;
            _ebookColocationPlanner = ebookColocationPlanner;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _rootFolderWatchingService = rootFolderWatchingService;
            _mediaFileAttributeService = mediaFileAttributeService;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _fileMutationSafetyService = fileMutationSafetyService;
            _logger = logger;
        }

        public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null)
        {
            if (bookFile == null || author == null)
            {
                return BookFileMovePlan.Skipped("The file or author is unavailable.");
            }

            var edition = GetEditionWithBookContext(bookFile);
            if (edition?.Book == null)
            {
                return BookFileMovePlan.Skipped($"Edition {bookFile.EditionId} is missing its book information.");
            }

            if (bookFile.Quality?.Quality == null)
            {
                return BookFileMovePlan.Skipped("The file has no media quality, so its media root cannot be determined.");
            }

            var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return BookFileMovePlan.Skipped("No root folder is configured for this media type.");
            }

            if (!TryGetPhysicalAuthorFolder(rootFolderPath, bookFile.Path, out var sourceAuthorFolderPath))
            {
                return BookFileMovePlan.Skipped("The file's current author folder cannot be determined from its configured root.");
            }

            var mediaType = GetEffectiveMediaType(bookFile);
            var namingConfig = _namingConfigService.GetConfig();
            var destinationAuthorFolderPath = sourceAuthorFolderPath;
            if (moveToCanonicalAuthorFolder)
            {
                var canonicalFolderName = _buildFileNames.GetAuthorFolder(author, namingConfig, mediaType);
                if (canonicalFolderName.IsNullOrWhiteSpace())
                {
                    return BookFileMovePlan.Skipped("The canonical author folder name could not be calculated.");
                }

                destinationAuthorFolderPath = Path.Combine(rootFolderPath, canonicalFolderName);
            }

            var newFileName = _buildFileNames.BuildBookFileName(author, edition, bookFile, namingConfig);
            var extension = Path.GetExtension(bookFile.Path);
            var fileNameOnly = Path.GetFileName(newFileName) + extension;
            var destinationPath = Path.Combine(destinationAuthorFolderPath, newFileName + extension);
            var colocationPlan = _ebookColocationPlanner.Plan(bookFile, author, edition, fileNameOnly, renameBatchContext);

            if (colocationPlan.Applies)
            {
                destinationPath = colocationPlan.PrimaryPath;
                if (!TryGetPhysicalAuthorFolder(rootFolderPath, destinationPath, out destinationAuthorFolderPath))
                {
                    return BookFileMovePlan.Skipped("The colocated destination author folder cannot be determined from its configured root.");
                }
            }

            return new BookFileMovePlan
            {
                CanOrganize = true,
                SourceAuthorFolderPath = sourceAuthorFolderPath,
                DestinationAuthorFolderPath = destinationAuthorFolderPath,
                DestinationPath = destinationPath,
                ReplicaPaths = colocationPlan.Applies ? colocationPlan.ReplicaPaths : null,
                ShouldCleanupReplicas = colocationPlan.ShouldCleanupReplicas,
                ShouldUpdateStoredAuthorPath = moveToCanonicalAuthorFolder && !colocationPlan.Applies,
                ColocationApplied = colocationPlan.Applies
            };
        }

        public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null)
        {
            var edition = GetEditionWithBookContext(bookFile);
            if (edition?.Book == null)
            {
                throw new InvalidOperationException($"Unable to move book file '{bookFile?.Path}' because edition '{bookFile?.EditionId}' is missing book context.");
            }

            bookFile.Edition ??= edition;
            if (!plan.CanOrganize)
            {
                throw new InvalidOperationException(plan.SkipReason);
            }

            if (plan.ColocationApplied)
            {
                EnsureBookFolder(bookFile, author, edition.Book, plan.DestinationPath, plan.DestinationAuthorFolderPath);
                _logger.Debug("Colocating ebook file: {0} to {1}", bookFile, plan.DestinationPath);

                if (bookFile.Path.PathNotEquals(plan.DestinationPath))
                {
                    TransferFile(bookFile, author, edition.Book, plan.DestinationPath, TransferMode.Move, plan.DestinationAuthorFolderPath);
                }

                ReconcileReplicaFiles(bookFile, author, edition.Book, plan.ReplicaPaths, preferHardlinks: _configService.CopyUsingHardlinks);
                return bookFile;
            }

            if (plan.ShouldCleanupReplicas)
            {
                CleanupReplicaFilesIfAny(bookFile);
            }

            EnsureBookFolder(bookFile, author, edition.Book, plan.DestinationPath, plan.DestinationAuthorFolderPath);

            _logger.Debug("Organizing book file: {0} to {1}", bookFile, plan.DestinationPath);

            return TransferFile(bookFile, author, edition.Book, plan.DestinationPath, TransferMode.Move, plan.DestinationAuthorFolderPath);
        }

        public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
        {
            var filePath = GetImportDestinationPath(bookFile, localBook, out var replicaPaths);

            if (replicaPaths != null)
            {
                EnsureTrackFolder(bookFile, localBook, filePath);
                _logger.Debug("Colocating ebook file: {0} to {1}", bookFile.Path, filePath);

                if (bookFile.Path.PathNotEquals(filePath))
                {
                    TransferFile(bookFile, localBook.Author, localBook.Book, filePath, TransferMode.Move);
                }

                ReconcileReplicaFiles(bookFile, localBook.Author, localBook.Book, replicaPaths, preferHardlinks: _configService.CopyUsingHardlinks);
                return bookFile;
            }

            EnsureTrackFolder(bookFile, localBook, filePath);

            _logger.Debug("Moving book file: {0} to {1}", bookFile.Path, filePath);

            return TransferFile(bookFile, localBook.Author, localBook.Book, filePath, TransferMode.Move);
        }

        public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook)
        {
            var filePath = GetImportDestinationPath(bookFile, localBook, out var replicaPaths);

            if (replicaPaths != null)
            {
                EnsureTrackFolder(bookFile, localBook, filePath);

                var primaryMode = _configService.CopyUsingHardlinks ? TransferMode.HardLinkOrCopy : TransferMode.Copy;
                if (bookFile.Path.PathNotEquals(filePath))
                {
                    _logger.Debug("{0} ebook file: {1} to {2}", primaryMode.HasFlag(TransferMode.HardLink) ? "Hardlinking" : "Copying", bookFile.Path, filePath);
                    TransferFile(bookFile, localBook.Author, localBook.Book, filePath, primaryMode);
                }
                else
                {
                    _logger.Debug("Ebook file already at destination: {0}", filePath);
                }

                ReconcileReplicaFiles(bookFile, localBook.Author, localBook.Book, replicaPaths, preferHardlinks: _configService.CopyUsingHardlinks);
                return bookFile;
            }

            EnsureTrackFolder(bookFile, localBook, filePath);

            if (_configService.CopyUsingHardlinks)
            {
                _logger.Debug("Hardlinking book file: {0} to {1}", bookFile.Path, filePath);
                return TransferFile(bookFile, localBook.Author, localBook.Book, filePath, TransferMode.HardLinkOrCopy);
            }

            _logger.Debug("Copying book file: {0} to {1}", bookFile.Path, filePath);
            return TransferFile(bookFile, localBook.Author, localBook.Book, filePath, TransferMode.Copy);
        }

        public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook)
        {
            return GetImportDestinationPath(bookFile, localBook, out _);
        }

        private string GetImportDestinationPath(BookFile bookFile, LocalBook localBook, out List<string> replicaPaths)
        {
            replicaPaths = null;

            var newFileName = _buildFileNames.BuildBookFileName(localBook.Author, localBook.Edition, bookFile);
            var extension = Path.GetExtension(localBook.Path);
            var fileNameOnly = Path.GetFileName(newFileName) + extension;

            var bookPath = _authorPathBuilder.BuildPathForQuality(localBook.Author, bookFile.Quality.Quality, useExistingRelativeFolder: false);
            var filePath = Path.Combine(bookPath, newFileName + extension);

            var colocationPlan = _ebookColocationPlanner.Plan(bookFile, localBook.Author, localBook.Edition, fileNameOnly);
            if (colocationPlan.Applies)
            {
                replicaPaths = colocationPlan.ReplicaPaths;
                return colocationPlan.PrimaryPath;
            }

            if (colocationPlan.ShouldCleanupReplicas)
            {
                CleanupReplicaFilesIfAny(bookFile);
            }

            return filePath;
        }

        private void CleanupReplicaFilesIfAny(BookFile bookFile)
        {
            if (bookFile?.ReplicaPaths == null || bookFile.ReplicaPaths.Count == 0)
            {
                return;
            }

            foreach (var replicaPath in bookFile.ReplicaPaths.Distinct(PathEqualityComparer.Instance))
            {
                TryDeleteReplica(replicaPath);
            }

            bookFile.ReplicaPaths = new List<string>();
        }

        private void ReconcileReplicaFiles(BookFile bookFile, Author author, Book book, List<string> desiredReplicaPaths, bool preferHardlinks = true)
        {
            desiredReplicaPaths ??= new List<string>();

            var desired = desiredReplicaPaths
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            var existing = (bookFile.ReplicaPaths ?? new List<string>())
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();
            var existingSet = new HashSet<string>(existing, PathEqualityComparer.Instance);

            // Remove stale managed replicas.
            foreach (var oldReplica in existing)
            {
                if (!desired.Any(p => p.PathEquals(oldReplica)))
                {
                    TryDeleteReplica(oldReplica);
                }
            }

            var kept = new List<string>();
            var sourcePath = bookFile.Path;

            // Ensure desired replicas exist on disk.
            foreach (var replicaPath in desired)
            {
                if (replicaPath.PathEquals(sourcePath))
                {
                    continue;
                }

                var isManagedReplica = existingSet.Contains(replicaPath);
                if (_diskProvider.FileExists(replicaPath))
                {
                    // If this is a previously-managed replica, recreate it to keep content in sync with the canonical file
                    // (e.g., after an upgrade/reimport that replaces the ebook).
                    if (isManagedReplica)
                    {
                        TryDeleteReplica(replicaPath);
                    }
                    else
                    {
                        // Don't overwrite (or later delete) user-managed files that happen to collide with our replica path.
                        _logger.Warn("Ebook replica destination already exists, leaving it unmanaged: {0}", replicaPath);
                        continue;
                    }
                }

                try
                {
                    var mode = preferHardlinks ? TransferMode.HardLinkOrCopy : TransferMode.Copy;
                    if (!_diskProvider.FileExists(replicaPath))
                    {
                        _diskTransferService.TransferFile(sourcePath, replicaPath, mode, overwrite: false);
                        _mediaFileAttributeService.SetFilePermissions(replicaPath);
                    }

                    if (_diskProvider.FileExists(replicaPath))
                    {
                        kept.Add(replicaPath);
                    }
                }
                catch (FileAlreadyExistsException)
                {
                    if (isManagedReplica)
                    {
                        kept.Add(replicaPath);
                    }
                    else
                    {
                        _logger.Warn("Ebook replica destination already exists, leaving it unmanaged: {0}", replicaPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to create ebook replica: {0}", replicaPath);
                }
            }

            bookFile.ReplicaPaths = kept;
        }

        private void TryDeleteReplica(string replicaPath)
        {
            if (replicaPath.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                if (_diskProvider.FileExists(replicaPath))
                {
                    _recycleBinProvider.DeleteFile(replicaPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to delete managed ebook replica: {0}", replicaPath);
            }
        }

        private BookFile TransferFile(BookFile bookFile, Author author, Book book, string destinationFilePath, TransferMode mode, string authorFolderPath = null)
        {
            Ensure.That(bookFile, () => bookFile).IsNotNull();
            Ensure.That(author, () => author).IsNotNull();
            Ensure.That(destinationFilePath, () => destinationFilePath).IsValidPath(PathValidationType.CurrentOs);

            var bookFilePath = bookFile.Path;

            if (!_diskProvider.FileExists(bookFilePath))
            {
                throw new FileNotFoundException("Book file path does not exist", bookFilePath);
            }

            if (bookFilePath == destinationFilePath)
            {
                throw new SameFilenameException("File not moved, source and destination are the same", bookFilePath);
            }

            var destinationFolder = Path.GetDirectoryName(destinationFilePath);
            if (!destinationFolder.IsNullOrWhiteSpace() && !_diskProvider.FolderWritable(destinationFolder))
            {
                throw BuildFolderWriteAccessException(destinationFilePath, destinationFolder);
            }

            _rootFolderWatchingService.ReportFileSystemChangeBeginning(bookFilePath, destinationFilePath);
            var actualTransferMode = _diskTransferService.TransferFile(bookFilePath, destinationFilePath, mode);

            bookFile.Path = destinationFilePath;
            _fileMutationSafetyService.PrepareImportDestination(bookFile, actualTransferMode);

            _updateBookFileService.ChangeFileDateForFile(bookFile, author, book);

            try
            {
                var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
                if (authorFolderPath.IsNullOrWhiteSpace())
                {
                    TryGetPhysicalAuthorFolder(rootFolderPath, destinationFilePath, out authorFolderPath);
                }

                if (authorFolderPath.IsNotNullOrWhiteSpace())
                {
                    _mediaFileAttributeService.SetFolderLastWriteTime(authorFolderPath, bookFile.DateAdded);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to set last write time");
            }

            _mediaFileAttributeService.SetFilePermissions(destinationFilePath);

            return bookFile;
        }

        private void EnsureTrackFolder(BookFile bookFile, LocalBook localBook, string filePath)
        {
            var rootFolderPath = localBook.Author.GetRootFolderForQuality(bookFile.Quality.Quality);
            if (!TryGetPhysicalAuthorFolder(rootFolderPath, filePath, out var authorFolderPath))
            {
                authorFolderPath = _authorPathBuilder.BuildPathForQuality(localBook.Author, bookFile.Quality.Quality, useExistingRelativeFolder: false);
            }

            EnsureBookFolder(bookFile, localBook.Author, localBook.Book, filePath, authorFolderPath);
        }

        private Edition GetEditionWithBookContext(BookFile bookFile)
        {
            if (bookFile == null)
            {
                return null;
            }

            var edition = bookFile.Edition ?? _editionService.GetEdition(bookFile.EditionId);
            if (edition != null && edition.Book == null && edition.BookId > 0)
            {
                edition = _editionService
                    .GetEditionsByBook(edition.BookId)
                    ?.FirstOrDefault(candidate => candidate.Id == edition.Id) ?? edition;
            }

            return edition;
        }

        internal static bool TryGetPhysicalAuthorFolder(string rootFolderPath, string filePath, out string authorFolderPath)
        {
            authorFolderPath = null;
            if (rootFolderPath.IsNullOrWhiteSpace() ||
                filePath.IsNullOrWhiteSpace() ||
                rootFolderPath.PathEquals(filePath) ||
                !rootFolderPath.IsParentPath(filePath))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(rootFolderPath, filePath);
            if (relativePath.IsNullOrWhiteSpace() ||
                relativePath == "." ||
                relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            var segments = relativePath
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            authorFolderPath = Path.Combine(rootFolderPath, segments[0]);
            return true;
        }

        private static string GetEffectiveMediaType(BookFile bookFile)
        {
            var mediaType = bookFile.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && bookFile.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            return mediaType;
        }

        private void EnsureBookFolder(BookFile bookFile, Author author, Book book, string filePath, string authorFolder)
        {
            var trackFolder = Path.GetDirectoryName(filePath);
            var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
            var rootFolder = new OsPath(rootFolderPath).FullPath;

            if (!_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException(string.Format("Root folder '{0}' was not found.", rootFolder));
            }

            var changed = false;
            var newEvent = new TrackFolderCreatedEvent(author, bookFile);

            _rootFolderWatchingService.ReportFileSystemChangeBeginning(authorFolder, trackFolder);

            if (!_diskProvider.FolderExists(authorFolder))
            {
                CreateFolder(authorFolder);
                newEvent.AuthorFolder = authorFolder;
                changed = true;
            }

            if (authorFolder.PathNotEquals(trackFolder) && !_diskProvider.FolderExists(trackFolder))
            {
                CreateFolder(trackFolder);
                newEvent.TrackFolder = trackFolder;
                changed = true;
            }

            if (changed)
            {
                _eventAggregator.PublishEvent(newEvent);
            }
        }

        private void CreateFolder(string directoryName)
        {
            Ensure.That(directoryName, () => directoryName).IsNotNullOrWhiteSpace();

            var parentFolder = new OsPath(directoryName).Directory.FullPath;
            if (!_diskProvider.FolderExists(parentFolder))
            {
                CreateFolder(parentFolder);
            }

            if (_diskProvider.FolderExists(directoryName))
            {
                _mediaFileAttributeService.SetFolderPermissions(directoryName);
                return;
            }

            if (!_diskProvider.FolderWritable(parentFolder))
            {
                throw BuildFolderCreateAccessException(directoryName, parentFolder);
            }

            try
            {
                _diskProvider.CreateFolder(directoryName);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw BuildFolderCreateAccessException(directoryName, parentFolder, ex);
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "Unable to create directory: {0}", directoryName);
                if (!_diskProvider.FolderExists(directoryName))
                {
                    throw;
                }
            }

            _mediaFileAttributeService.SetFolderPermissions(directoryName);
        }

        private static UnauthorizedAccessException BuildFolderCreateAccessException(string directoryName, string parentFolder, Exception innerException = null)
        {
            var user = ProcessUserInfo.GetUserNameWithIds();
            var dockerEnv = ProcessUserInfo.GetDockerUserEnvSummary();
            var dockerHint = dockerEnv == null ? string.Empty : $" ({dockerEnv})";
            var message = $"Cannot create media folder '{directoryName}' because parent folder '{parentFolder}' is not writable by the Chaptarr process '{user}'{dockerHint}. " +
                          "Fix the host folder ownership/permissions or run Chaptarr with matching PUID/PGID. " +
                          "If you tested this from a Docker shell, make sure you tested as the app user, not root.";

            return innerException == null
                ? new UnauthorizedAccessException(message)
                : new UnauthorizedAccessException(message, innerException);
        }

        private static UnauthorizedAccessException BuildFolderWriteAccessException(string destinationFilePath, string destinationFolder)
        {
            var user = ProcessUserInfo.GetUserNameWithIds();
            var dockerEnv = ProcessUserInfo.GetDockerUserEnvSummary();
            var dockerHint = dockerEnv == null ? string.Empty : $" ({dockerEnv})";
            var message = $"Cannot import media file '{destinationFilePath}' because destination folder '{destinationFolder}' is not writable by the Chaptarr process '{user}'{dockerHint}. " +
                          "Fix the host folder ownership/permissions or run Chaptarr with matching PUID/PGID. " +
                          "If you tested this from a Docker shell, make sure you tested as the app user, not root.";

            return new UnauthorizedAccessException(message);
        }

    }
}
