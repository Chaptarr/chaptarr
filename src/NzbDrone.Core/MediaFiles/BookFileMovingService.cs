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
        BookFile MoveBookFile(BookFile bookFile, Author author, RenameBatchContext renameBatchContext = null);
        BookFile MoveBookFile(BookFile bookFile, LocalBook localBook);
        BookFile CopyBookFile(BookFile bookFile, LocalBook localBook);
        string GetImportDestinationPath(BookFile bookFile, LocalBook localBook);
    }

    public class BookFileMovingService : IMoveBookFiles
    {
        private readonly IEditionService _editionService;
        private readonly IUpdateBookFileService _updateBookFileService;
        private readonly IBuildFileNames _buildFileNames;
        private readonly IEbookColocationPlanner _ebookColocationPlanner;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly IAuthorFolderPathResolver _authorFolderPathResolver;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;
        private readonly Logger _logger;

        public BookFileMovingService(IEditionService editionService,
                                      IUpdateBookFileService updateBookFileService,
                                      IBuildFileNames buildFileNames,
                                      IEbookColocationPlanner ebookColocationPlanner,
                                      IDiskTransferService diskTransferService,
                                      IDiskProvider diskProvider,
                                      IRecycleBinProvider recycleBinProvider,
                                      IRootFolderWatchingService rootFolderWatchingService,
                                      IMediaFileAttributeService mediaFileAttributeService,
                                      IAuthorFolderPathResolver authorFolderPathResolver,
                                      IEventAggregator eventAggregator,
                                      IConfigService configService,
                                      IFileMutationSafetyService fileMutationSafetyService,
                                      Logger logger)
        {
            _editionService = editionService;
            _updateBookFileService = updateBookFileService;
            _buildFileNames = buildFileNames;
            _ebookColocationPlanner = ebookColocationPlanner;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _rootFolderWatchingService = rootFolderWatchingService;
            _mediaFileAttributeService = mediaFileAttributeService;
            _authorFolderPathResolver = authorFolderPathResolver;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _fileMutationSafetyService = fileMutationSafetyService;
            _logger = logger;
        }

        public BookFile MoveBookFile(BookFile bookFile, Author author, RenameBatchContext renameBatchContext = null)
        {
            // Prefer the edition already loaded on the BookFile (includes Book/Author/Series context),
            // falling back to a direct lookup when needed.
            var edition = bookFile.Edition ?? _editionService.GetEdition(bookFile.EditionId);
            if (edition != null && edition.Book == null && edition.BookId > 0)
            {
                edition = _editionService
                    .GetEditionsByBook(edition.BookId)
                    ?.FirstOrDefault(e => e.Id == edition.Id) ?? edition;
            }

            if (edition?.Book == null)
            {
                throw new InvalidOperationException($"Unable to move book file '{bookFile?.Path}' because edition '{bookFile?.EditionId}' is missing book context.");
            }

            bookFile.Edition ??= edition;
            var mediaType = GetEffectiveMediaType(bookFile);
            var newFileName = _buildFileNames.BuildBookFileName(author, edition, bookFile);
            var extension = Path.GetExtension(bookFile.Path);
            var fileNameOnly = Path.GetFileName(newFileName) + extension;

            // Get quality-specific root folder
            var rootFolder = author.GetRootFolderForQuality(bookFile.Quality.Quality);
            var bookPath = _authorFolderPathResolver.GetAuthorPath(rootFolder, author, mediaType);
            var filePath = Path.Combine(bookPath, newFileName + extension);

            var colocationPlan = _ebookColocationPlanner.Plan(bookFile, author, edition, fileNameOnly, renameBatchContext);
            if (colocationPlan.Applies)
            {
                filePath = colocationPlan.PrimaryPath;
                EnsureBookFolder(bookFile, author, edition.Book, filePath);
                _logger.Debug("Colocating ebook file: {0} to {1}", bookFile, filePath);

                if (bookFile.Path.PathNotEquals(filePath))
                {
                    TransferFile(bookFile, author, edition.Book, filePath, TransferMode.Move);
                }

                ReconcileReplicaFiles(bookFile, author, edition.Book, colocationPlan.ReplicaPaths, preferHardlinks: _configService.CopyUsingHardlinks);
                return bookFile;
            }

            if (colocationPlan.ShouldCleanupReplicas)
            {
                CleanupReplicaFilesIfAny(bookFile);
            }

            EnsureBookFolder(bookFile, author, edition.Book, filePath);

            _logger.Debug("Renaming book file: {0} to {1}", bookFile, filePath);

            return TransferFile(bookFile, author, edition.Book, filePath, TransferMode.Move);
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

            var rootFolder = localBook.Author.GetRootFolderForQuality(bookFile.Quality.Quality);
            var mediaType = GetEffectiveMediaType(bookFile);
            var bookPath = _authorFolderPathResolver.GetAuthorPath(rootFolder, localBook.Author, mediaType);
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

        private BookFile TransferFile(BookFile bookFile, Author author, Book book, string destinationFilePath, TransferMode mode)
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
                // Get quality-specific author folder path
                var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
                var mediaType = GetEffectiveMediaType(bookFile);
                var authorFolderPath = _authorFolderPathResolver.GetAuthorPath(rootFolderPath, author, mediaType);
                _mediaFileAttributeService.SetFolderLastWriteTime(authorFolderPath, bookFile.DateAdded);
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
            EnsureBookFolder(bookFile, localBook.Author, localBook.Book, filePath);
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

        private void EnsureBookFolder(BookFile bookFile, Author author, Book book, string filePath)
        {
            var trackFolder = Path.GetDirectoryName(filePath);

            // Get quality-specific root folder and author path
            var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
            var mediaType = GetEffectiveMediaType(bookFile);
            var authorFolder = _authorFolderPathResolver.GetAuthorPath(rootFolderPath, author, mediaType);

            var bookFolder = authorFolder; // For now, we're using the author folder as the book folder
            var rootFolder = new OsPath(rootFolderPath).FullPath;

            if (!_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException(string.Format("Root folder '{0}' was not found.", rootFolder));
            }

            var changed = false;
            var newEvent = new TrackFolderCreatedEvent(author, bookFile);

            _rootFolderWatchingService.ReportFileSystemChangeBeginning(authorFolder, bookFolder, trackFolder);

            if (!_diskProvider.FolderExists(authorFolder))
            {
                CreateFolder(authorFolder);
                newEvent.AuthorFolder = authorFolder;
                changed = true;
            }

            if (authorFolder != bookFolder && !_diskProvider.FolderExists(bookFolder))
            {
                CreateFolder(bookFolder);
                newEvent.BookFolder = bookFolder;
                changed = true;
            }

            if (bookFolder != trackFolder && !_diskProvider.FolderExists(trackFolder))
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
