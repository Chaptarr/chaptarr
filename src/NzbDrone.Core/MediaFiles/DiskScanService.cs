using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.ProgressMessaging;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// Clean implementation of DiskScanService that follows the import bible.
    /// This service ONLY discovers files and passes them to ImportOrchestrator.
    /// It does NOT make import decisions or modify the library.
    /// </summary>
    public class DiskScanService : IDiskScanService, IExecute<RescanFoldersCommand>
    {
        public static readonly Regex ExcludedSubFoldersRegex = new Regex(@"(?:\\|\/|^)(?:extras|@eadir|extrafanart|plex versions|\.[^\\/]+)(?:\\|\/)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex ExcludedFilesRegex = new Regex(@"^\._|^Thumbs\.db$|^\.DS_store$|\.partial~$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int LargeScanCompactionFileThreshold = 10000;
        private const int SpecificScanFolderMemoryCheckpointInterval = 25;
        private static readonly TimeSpan LargeScanCompactionDurationThreshold = TimeSpan.FromMinutes(10);

        private sealed class InventoryReconciliationException : Exception
        {
            public InventoryReconciliationException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly ICalibreProxy _calibre;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IIngestQueueRepository _ingestQueueRepository;
        private readonly IImportOrchestrator _importOrchestrator;
        private readonly IAuthorService _authorService;
        private readonly IMediaFileTableCleanupService _mediaFileTableCleanupService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DiskScanService(
            IConfigService configService,
            IDiskProvider diskProvider,
            ICalibreProxy calibre,
            IMediaFileService mediaFileService,
            IMetadataTagService metadataTagService,
            IIngestQueueRepository ingestQueueRepository,
            IImportOrchestrator importOrchestrator, // Using ImportOrchestrator instead of IMakeImportDecision
            IAuthorService authorService,
            IRootFolderService rootFolderService,
            IMediaFileTableCleanupService mediaFileTableCleanupService,
            IManageCommandQueue commandQueueManager,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _configService = configService;
            _diskProvider = diskProvider;
            _calibre = calibre;
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _ingestQueueRepository = ingestQueueRepository;
            _importOrchestrator = importOrchestrator;
            _authorService = authorService;
            _mediaFileTableCleanupService = mediaFileTableCleanupService;
            _rootFolderService = rootFolderService;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Scan(List<string> folders = null, FilterFilesType filter = FilterFilesType.Known, List<int> authorIds = null, bool isInitialImport = false, CancellationToken cancellationToken = default)
        {
            _logger.Debug("[DISK-SCAN] Starting scan - folders: [{0}], filter: {1}",
                folders != null ? string.Join(", ", folders) : "all root folders",
                filter);

            if (folders == null)
            {
                folders = _rootFolderService.All().Select(x => x.Path).ToList();
            }

            LogMemorySnapshot("Disk scan start ({0} folders)", folders.Count);

            var scanStopwatch = Stopwatch.StartNew();

            // Emit initial scanning stage event
            if (isInitialImport)
            {
                var startEvt = new ImportStageProgressEvent(
                    ImportStage.ScanningFolders,
                    $"Scanning {folders.Count} root folders for media files",
                    0,
                    folders.Count);
                startEvt.CommandId = ProgressMessaging.ProgressMessageContext.CommandModel?.Id;
                _eventAggregator.PublishEvent(startEvt);
            }

            var processedFolders = 0;
            var totalScannedFiles = 0;

            foreach (var folder in folders)
            {
                CheckForPauseAndWait(ref cancellationToken);

                var rootFolder = _rootFolderService.GetBestRootFolder(folder);
                if (rootFolder == null)
                {
                    _logger.Error("Not scanning {0}, it's not a subdirectory of a defined root folder", folder);
                    continue;
                }

                if (!_diskProvider.FolderExists(folder))
                {
                    _logger.Warn("Skipping scan cleanup for {0} because the folder is not visible. This may be a missing mount or unavailable root folder.", folder);
                    continue;
                }

                // Don't show scanning folder notification - it interferes with import progress display
                // _logger.ProgressInfo("Scanning folder {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);
                _logger.Debug("Scanning folder {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);
                LogMemorySnapshot("Disk scan folder start {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);

                try
                {
                    // Process this folder using ImportOrchestrator
                    // This follows the 12-step workflow from the import bible
                    // Get command ID from context for progress tracking
                    var commandId = ProgressMessageContext.CommandModel?.Id;
                    var orchestratorTask = _importOrchestrator.ProcessFilesAsync(
                        folder,
                        rootFolder,
                        commandId,
                        filter: filter);
                    orchestratorTask.Wait(cancellationToken);
                    var result = orchestratorTask.Result;
                    LogMemorySnapshot("Disk scan folder after orchestration {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);

                    _logger.Debug("[DISK-SCAN] Processed folder {0}: {1} files imported, {2} unmapped, {3} authors added",
                        folder, result.ImportedFiles.Count, result.UnmappedFiles.Count, result.AddedAuthors.Count);
                    totalScannedFiles += result.ScannedFilePaths?.Count ?? 0;

                    // Reconcile inventory after all matching/apply work: every still-existing supported file seen
                    // under the root must have exactly one mapped or EditionId=0 BookFile row.
                    EnsureScannedFilesVisible(result, includeReportedReasons: true);
                    LogMemorySnapshot("Disk scan folder after inventory reconciliation {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);

                    // Only clean after a complete enumeration. Import results are not a safe substitute for the full scan list.
                    if (!result.CleanupSafe)
                    {
                        _logger.Warn("Skipping scan cleanup for {0} because file enumeration did not complete safely", folder);
                    }
                    else if (!result.ScannedFilePaths.Any())
                    {
                        _logger.Warn("Skipping scan cleanup for {0} because the scan found no media files. This avoids wiping tracked files when a mount is visible but empty.", folder);
                    }
                    else
                    {
                        CleanMediaFiles(folder, result.ScannedFilePaths, rootFolder);
                    }

                    LogMemorySnapshot("Disk scan folder after cleanup {0}/{1}: {2}", processedFolders + 1, folders.Count, folder);
                }
                catch (InventoryReconciliationException ex)
                {
                    _logger.Error(ex, "Root inventory reconciliation failed for folder: {0}", folder);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to scan folder: {0}", folder);
                }

                processedFolders++;

                // Update progress
                var progEvt = new ImportStageProgressEvent(
                    ImportStage.ScanningFolders,
                    $"Scanning {folders.Count} root folders for media files",
                    processedFolders,
                    folders.Count);
                progEvt.CommandId = ProgressMessaging.ProgressMessageContext.CommandModel?.Id;
                _eventAggregator.PublishEvent(progEvt);
            }

            scanStopwatch.Stop();
            _logger.Debug("[DISK-SCAN] Scan completed in {0}", scanStopwatch.Elapsed);
            LogMemorySnapshot("Disk scan folders complete ({0} files, {1})", totalScannedFiles, scanStopwatch.Elapsed);

            // Notify completion for each author
            var authors = _authorService.GetAuthors(authorIds ?? new List<int>());
            foreach (var author in authors)
            {
                CheckForPauseAndWait(ref cancellationToken);
                CompletedScanning(author);
            }

            authors = null;
            LogMemorySnapshot("Disk scan author notifications complete ({0} files, {1})", totalScannedFiles, scanStopwatch.Elapsed);

            // Final progress update
            var doneEvt = new ImportStageProgressEvent(
                ImportStage.ImportComplete,
                "Library scan completed",
                folders.Count,
                folders.Count);
            doneEvt.CommandId = ProgressMessaging.ProgressMessageContext.CommandModel?.Id;
            _eventAggregator.PublishEvent(doneEvt);

            CompactAfterLargeScanIfNeeded(totalScannedFiles, scanStopwatch.Elapsed);
        }

        private void LogMemorySnapshot(string message, params object[] args)
        {
            if (!_logger.IsDebugEnabled)
            {
                return;
            }

            try
            {
                var formatted = args == null || args.Length == 0 ? message : string.Format(message, args);
                _logger.Debug("[MEMORY] {0}: {1}", formatted, MemorySnapshot.CaptureDetailed());
            }
            catch
            {
                // Diagnostics must never affect scanning.
            }
        }

        private void CompactAfterLargeScanIfNeeded(int scannedFileCount, TimeSpan elapsed)
        {
            if (scannedFileCount < LargeScanCompactionFileThreshold &&
                elapsed < LargeScanCompactionDurationThreshold)
            {
                return;
            }

            try
            {
                _logger.Debug("[MEMORY] Large scan complete ({0} files, {1}); before compacting GC: {2}",
                    scannedFileCount,
                    elapsed,
                    MemorySnapshot.CaptureDetailed());

                var nativeTrimmed = MemorySnapshot.ReleaseUnusedMemory();

                _logger.Debug("[MEMORY] Large scan complete ({0} files, {1}); after compacting GC/native trim (nativeTrimmed={2}): {3}",
                    scannedFileCount,
                    elapsed,
                    nativeTrimmed,
                    MemorySnapshot.CaptureDetailed());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[MEMORY] Large scan compacting GC failed");
            }
        }

        private void ScanSpecificFiles(List<string> filePaths, FilterFilesType filter, bool isInitialImport, CancellationToken cancellationToken)
        {
            _logger.Debug("[DISK-SCAN] Processing {0} specific file paths", filePaths.Count);
            var scanStopwatch = Stopwatch.StartNew();
            LogMemorySnapshot("Specific-file scan start ({0} files)", filePaths.Count);

            // A user-triggered specific-file scan is an explicit retry. Do not let stale
            // staging rows from a previous author/book ownership block the file from
            // being staged and matched again.
            PurgeSpecificFileStaging(filePaths);
            LogMemorySnapshot("Specific-file scan after staging purge ({0} files)", filePaths.Count);

            // Group files by their parent folder to use ImportOrchestrator efficiently
            // Don't filter by file existence - let the import process handle missing files
            var filesByFolder = filePaths
                .GroupBy(p => Path.GetDirectoryName(p))
                .ToList();
            LogMemorySnapshot("Specific-file scan after grouping ({0} files, {1} folders)", filePaths.Count, filesByFolder.Count);
            
            // Log files that don't exist for debugging
            var missingFiles = filePaths.Where(p => !_diskProvider.FileExists(p)).ToList();
            if (missingFiles.Any())
            {
                _logger.Debug("[DISK-SCAN] {0} files in database don't exist at stored paths", missingFiles.Count);
            }
            LogMemorySnapshot("Specific-file scan after missing-file check ({0} missing, {1} files)", missingFiles.Count, filePaths.Count);

            _logger.Debug("[DISK-SCAN] Grouped into {0} folders", filesByFolder.Count);

            var processedFolders = 0;
            foreach (var folderGroup in filesByFolder)
            {
                CheckForPauseAndWait(ref cancellationToken);

                var folder = folderGroup.Key;
                var folderFiles = folderGroup.ToList();
                processedFolders++;
                var rootFolder = _rootFolderService.GetBestRootFolder(folder);

                if (rootFolder == null)
                {
                    _logger.Warn("Cannot process files in {0} - not in a root folder", folder);
                    continue;
                }

                try
                {
                    if (ShouldLogSpecificScanCheckpoint(processedFolders, filesByFolder.Count, folderFiles.Count))
                    {
                        LogMemorySnapshot("Specific-file scan folder start ({0}/{1}, {2} files, '{3}')",
                            processedFolders,
                            filesByFolder.Count,
                            folderFiles.Count,
                            folder);
                    }

                    // Process using ImportOrchestrator
                    // Get command ID from context for progress tracking
                    var commandId = ProgressMessageContext.CommandModel?.Id;
                    var orchestratorTask = _importOrchestrator.ProcessFilesAsync(
                        folder,
                        rootFolder,
                        commandId,
                        folderFiles,
                        filter);
                    orchestratorTask.Wait(cancellationToken);
                    var result = orchestratorTask.Result;

                    _logger.Debug("[DISK-SCAN] Processed {0} files in folder {1}: {2} imported, {3} unmapped",
                        folderFiles.Count, folder, result.ImportedFiles.Count, result.UnmappedFiles.Count);
                    LogMemorySnapshot("Specific-file scan after orchestrator ({0}/{1}, {2} files, {3} imported, {4} unmapped, '{5}')",
                        processedFolders,
                        filesByFolder.Count,
                        folderFiles.Count,
                        result.ImportedFiles.Count,
                        result.UnmappedFiles.Count,
                        folder);

                    EnsureScannedFilesVisible(result, includeReportedReasons: true);
                    LogMemorySnapshot("Specific-file scan after inventory reconciliation ({0}/{1}, {2} seen, '{3}')",
                        processedFolders,
                        filesByFolder.Count,
                        result.ScannedFilePaths?.Count ?? 0,
                        folder);

                    ReleaseMemoryDuringLongSpecificScanIfNeeded(processedFolders, filesByFolder.Count, folder);
                }
                catch (InventoryReconciliationException ex)
                {
                    _logger.Error(ex, "Root inventory reconciliation failed for files in folder: {0}", folder);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process files in folder: {0}", folder);
                    LogMemorySnapshot("Specific-file scan after failed folder ({0}/{1}, {2} files, '{3}')",
                        processedFolders,
                        filesByFolder.Count,
                        folderFiles.Count,
                        folder);
                }
            }

            scanStopwatch.Stop();
            LogMemorySnapshot("Specific-file scan complete ({0} files, {1} folders, {2})", filePaths.Count, filesByFolder.Count, scanStopwatch.Elapsed);
            CompactAfterLargeScanIfNeeded(filePaths.Count, scanStopwatch.Elapsed);
        }

        private void ReleaseMemoryDuringLongSpecificScanIfNeeded(int processedFolders, int totalFolders, string folder)
        {
            if (totalFolders < SpecificScanFolderMemoryCheckpointInterval ||
                processedFolders <= 0 ||
                processedFolders >= totalFolders ||
                processedFolders % SpecificScanFolderMemoryCheckpointInterval != 0)
            {
                return;
            }

            try
            {
                _logger.Debug("[MEMORY] Specific-file scan checkpoint before compacting GC/native trim ({0}/{1}, '{2}'): {3}",
                    processedFolders,
                    totalFolders,
                    folder,
                    MemorySnapshot.CaptureDetailed());

                var nativeTrimmed = MemorySnapshot.ReleaseUnusedMemory();

                _logger.Debug("[MEMORY] Specific-file scan checkpoint after compacting GC/native trim ({0}/{1}, nativeTrimmed={2}, '{3}'): {4}",
                    processedFolders,
                    totalFolders,
                    nativeTrimmed,
                    folder,
                    MemorySnapshot.CaptureDetailed());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[MEMORY] Specific-file scan checkpoint compacting failed ({0}/{1}, '{2}')",
                    processedFolders,
                    totalFolders,
                    folder);
            }
        }

        private static bool ShouldLogSpecificScanCheckpoint(int processedFolders, int totalFolders, int fileCount)
        {
            return processedFolders == 1 ||
                   processedFolders == totalFolders ||
                   processedFolders % SpecificScanFolderMemoryCheckpointInterval == 0 ||
                   fileCount >= SpecificScanFolderMemoryCheckpointInterval;
        }

        private void PurgeSpecificFileStaging(IEnumerable<string> filePaths)
        {
            if (_ingestQueueRepository == null || filePaths == null)
            {
                return;
            }

            var paths = filePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            if (!paths.Any())
            {
                return;
            }

            int purged;
            try
            {
                purged = _ingestQueueRepository.PurgePaths(paths);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[DISK-SCAN] Failed to purge stale staging state for {0} explicit file retries", paths.Count);
                return;
            }

            if (purged > 0)
            {
                _logger.Debug("[DISK-SCAN] Purged {0} stale staging rows for explicit file retry", purged);
            }
        }

        public IFileInfo[] GetBookFiles(string path, bool allDirectories = true)
        {
            IEnumerable<IFileInfo> filesOnDisk;
            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            if (rootFolder != null && rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null)
            {
                _logger.Info("Getting book list from calibre for {0}", path);
                var paths = _calibre.GetAllBookFilePaths(rootFolder.CalibreSettings);
                var folderPaths = paths.Where(x => path.IsParentPath(x));
                filesOnDisk = folderPaths.Select(x => _diskProvider.GetFileInfo(x));
            }
            else
            {
                _logger.Debug("Scanning '{0}' for book files", path);
                filesOnDisk = _diskProvider.GetFileInfos(path, allDirectories);
            }

            var mediaFileList = filesOnDisk.Where(file => MediaFileExtensions.AllExtensions.Contains(file.Extension))
                .ToArray();

            _logger.Debug("{0} book files found in {1}", mediaFileList.Length, path);
            return mediaFileList;
        }

        public string[] GetNonBookFiles(string path, bool allDirectories = true)
        {
            _logger.Debug("Scanning '{0}' for non-book files", path);
            var filesOnDisk = _diskProvider.GetFiles(path, allDirectories).ToList();
            var mediaFileList = filesOnDisk.Where(file => !MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(file)))
                                           .ToList();
            _logger.Debug("{0} non-book files found in {1}", mediaFileList.Count, path);
            return mediaFileList.ToArray();
        }

        public List<IFileInfo> FilterFiles(string basePath, IEnumerable<IFileInfo> files)
        {
            return files.Where(file => !ExcludedSubFoldersRegex.IsMatch(basePath.GetRelativePath(file.FullName)))
                        .Where(file => !ExcludedFilesRegex.IsMatch(file.Name))
                        .ToList();
        }

        public List<string> FilterPaths(string basePath, IEnumerable<string> paths)
        {
            return paths.Where(file => !ExcludedSubFoldersRegex.IsMatch(basePath.GetRelativePath(file)))
                        .Where(file => !ExcludedFilesRegex.IsMatch(Path.GetFileName(file)))
                        .ToList();
        }

        private void CleanMediaFiles(string folder, List<string> currentFilePaths, RootFolder rootFolder = null)
        {
            _logger.Debug("Cleaning up media files in DB for folder: {0}", folder);
            
            // For mixed content folders, we need to partition cleanup by MediaType
            if (rootFolder != null && rootFolder.FolderType == FolderType.Mixed)
            {
                _logger.Debug("Mixed content folder detected, partitioning cleanup by MediaType");
                
                // Clean audiobook files separately
                _logger.Debug("Cleaning audiobook files");
                _mediaFileTableCleanupService.Clean(folder, currentFilePaths, "audiobook");
                
                // Clean ebook files separately  
                _logger.Debug("Cleaning ebook files");
                _mediaFileTableCleanupService.Clean(folder, currentFilePaths, "ebook");
            }
            else
            {
                // For single media type folders, use the original cleanup (no MediaType filtering)
                _mediaFileTableCleanupService.Clean(folder, currentFilePaths);
                // Do not delete files solely because their extension differs from the root folder type. Those files
                // remain visible as unmapped so users can match in place and organize them into the correct root.
            }
        }

        private void EnsureScannedFilesVisible(OrchestratorImportResult result, bool includeReportedReasons)
        {
            try
            {
                EnsureScannedFilesVisibleCore(result, includeReportedReasons);
            }
            catch (InventoryReconciliationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InventoryReconciliationException("Unable to prove that every observed root media file has exactly one BookFile row", ex);
            }
        }

        private void EnsureScannedFilesVisibleCore(OrchestratorImportResult result, bool includeReportedReasons)
        {
            if (result == null)
            {
                return;
            }

            const string inventoryReason = "INVENTORY_RECONCILIATION";
            var reasonByPath = new Dictionary<string, string>(PathEqualityComparer.Instance);

            foreach (var path in result.ScannedFilePaths ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    reasonByPath.TryAdd(path, inventoryReason);
                }
            }

            if (includeReportedReasons)
            {
                foreach (var unmapped in result.UnmappedFiles ?? new List<UnmappedFile>())
                {
                    if (!string.IsNullOrWhiteSpace(unmapped?.FilePath))
                    {
                        reasonByPath[unmapped.FilePath] = string.IsNullOrWhiteSpace(unmapped.Reason)
                            ? "UNMAPPED"
                            : unmapped.Reason;
                    }
                }
            }

            foreach (var failed in result.FailedFiles ?? new List<FailedFile>())
            {
                if (!string.IsNullOrWhiteSpace(failed?.FilePath))
                {
                    reasonByPath[failed.FilePath] = $"APPLY_FAILED:{failed.Reason ?? "UNKNOWN"}";
                }
            }

            if (reasonByPath.Count == 0)
            {
                return;
            }

            var paths = reasonByPath.Keys.ToList();
            var initiallyPersisted = _mediaFileService.GetFileWithPath(paths) ?? new List<BookFile>();
            var initiallyPersistedByPath = initiallyPersisted
                .Where(file => !string.IsNullOrWhiteSpace(file?.Path))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.ToList(), PathEqualityComparer.Instance);
            var duplicatePaths = initiallyPersistedByPath
                .Where(item => item.Value.Count != 1)
                .Select(item => item.Key)
                .ToList();
            if (duplicatePaths.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Root inventory reconciliation found duplicate BookFile paths: {string.Join(", ", duplicatePaths.Take(10))}");
            }

            var initiallyMissingPaths = paths
                .Where(path => !initiallyPersistedByPath.ContainsKey(path))
                .ToList();
            var pathsNeedingSave = initiallyMissingPaths
                .ToDictionary(path => path, path => reasonByPath[path], PathEqualityComparer.Instance);

            // Existing EditionId=0 rows are already visible. Revisit only apply failures so their scratchpad
            // provenance is refreshed; ordinary inventory reconciliation must not rewrite every row each scan.
            foreach (var item in initiallyPersistedByPath)
            {
                var reason = reasonByPath[item.Key];
                if (item.Value[0].EditionId == 0 &&
                    reason?.StartsWith("APPLY_FAILED:", StringComparison.Ordinal) == true)
                {
                    pathsNeedingSave[item.Key] = reason;
                }
            }

            if (pathsNeedingSave.Count > 0)
            {
                SaveUnmappedFiles(pathsNeedingSave
                    .Select(item => new UnmappedFile { FilePath = item.Key, Reason = item.Value })
                    .ToList());
            }

            if (initiallyMissingPaths.Count == 0)
            {
                _logger.Debug("[DISK-SCAN] Inventory reconciliation proved {0} media paths already have exactly one BookFile row", initiallyPersistedByPath.Count);
                return;
            }

            var persistedAfterSave = _mediaFileService.GetFileWithPath(initiallyMissingPaths) ?? new List<BookFile>();
            var persistedAfterSaveCounts = persistedAfterSave
                .Where(file => !string.IsNullOrWhiteSpace(file?.Path))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.Count(), PathEqualityComparer.Instance);
            duplicatePaths = persistedAfterSaveCounts
                .Where(item => item.Value != 1)
                .Select(item => item.Key)
                .ToList();
            var missingExistingPaths = new List<string>();

            foreach (var path in initiallyMissingPaths.Where(path => !persistedAfterSaveCounts.ContainsKey(path)))
            {
                var fileInfo = _diskProvider.GetFileInfo(path);
                if (fileInfo?.Exists == true)
                {
                    missingExistingPaths.Add(path);
                }
            }

            if (duplicatePaths.Count > 0 || missingExistingPaths.Count > 0)
            {
                var examples = duplicatePaths
                    .Concat(missingExistingPaths)
                    .Distinct(PathEqualityComparer.Instance)
                    .Take(10);
                throw new InvalidOperationException(
                    $"Root inventory reconciliation failed: missing={missingExistingPaths.Count}, duplicate={duplicatePaths.Count}. Examples: {string.Join(", ", examples)}");
            }

            _logger.Debug("[DISK-SCAN] Inventory reconciliation proved {0} media paths have exactly one BookFile row ({1} already represented, {2} reconciled)",
                initiallyPersistedByPath.Count + persistedAfterSaveCounts.Count,
                initiallyPersistedByPath.Count,
                persistedAfterSaveCounts.Count);
        }

        private void SaveUnmappedFiles(List<UnmappedFile> unmappedFiles)
        {
            if (!unmappedFiles.Any())
            {
                return;
            }

            _logger.Debug("[DISK-SCAN] Saving {0} unmapped files to database", unmappedFiles.Count);

            var reasonByPath = unmappedFiles
                .Where(file => !string.IsNullOrWhiteSpace(file?.FilePath))
                .GroupBy(file => file.FilePath, PathEqualityComparer.Instance)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(file => file.Reason).LastOrDefault(reason => !string.IsNullOrWhiteSpace(reason)),
                    PathEqualityComparer.Instance);

            var filePaths = unmappedFiles
                .Select(f => f?.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            if (!filePaths.Any())
            {
                return;
            }

            var existingByPath = (_mediaFileService.GetFileWithPath(filePaths) ?? new List<BookFile>())
                .Where(file => !string.IsNullOrWhiteSpace(file?.Path))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);

            var newUnmappedFiles = new List<BookFile>();
            var updatedUnmappedFiles = new List<BookFile>();

            bool TryHydrateUnmappedMetadata(BookFile bookFile, IFileInfo fileInfo)
            {
                if (bookFile == null || fileInfo == null || !fileInfo.Exists || _metadataTagService == null)
                {
                    return false;
                }

                var hasTags = bookFile.AllTags != null && bookFile.AllTags.Any();
                var hasDuration = MediaDuration.HasDuration(bookFile.DurationSeconds);
                var extension = Path.GetExtension(bookFile.Path ?? fileInfo.FullName);
                var isAudio = MediaFileExtensions.AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
                if (hasTags && (!isAudio || hasDuration))
                {
                    return false;
                }

                try
                {
                    var (tags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fileInfo);
                    var changed = false;

                    if (!hasTags && tags != null && tags.Any())
                    {
                        bookFile.AllTags = tags;
                        changed = true;
                    }

                    if (!hasDuration && MediaDuration.HasDuration(durationSeconds))
                    {
                        bookFile.DurationSeconds = durationSeconds;
                        changed = true;
                    }

                    return changed;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[DISK-SCAN] Failed to hydrate unmapped metadata for: {0}", bookFile.Path);
                    return false;
                }
            }

            foreach (var filePath in filePaths)
            {
                try
                {
                    if (existingByPath.TryGetValue(filePath, out var existingFile))
                    {
                        // Never downgrade an existing tracked file to "unmapped".
                        // Manual mappings (and previous successful imports) must survive rescans, even if matching fails.
                        if (existingFile.EditionId != 0)
                        {
                            _logger.Debug("[DISK-SCAN] Skipping unmapped save for already-tracked file (EditionId={0}): {1}",
                                existingFile.EditionId, filePath);
                        }
                        else
                        {
                            var existingInfo = _diskProvider.GetFileInfo(filePath);
                            var changed = TryHydrateUnmappedMetadata(existingFile, existingInfo);
                            reasonByPath.TryGetValue(filePath, out var existingReason);
                            if (!string.IsNullOrWhiteSpace(existingReason) &&
                                existingReason.StartsWith("APPLY_FAILED:", StringComparison.Ordinal) &&
                                !string.Equals(existingFile.MatchDetails, existingReason, StringComparison.Ordinal))
                            {
                                existingFile.MatchDetails = existingReason;
                                changed = true;
                            }

                            if (changed)
                            {
                                updatedUnmappedFiles.Add(existingFile);
                            }
                        }

                        continue;
                    }

                    // Create new unmapped file record
                    var fileInfo = _diskProvider.GetFileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    // Detect quality from file extension
                    var extension = Path.GetExtension(filePath);
                    var quality = MediaFileExtensions.GetQualityForExtension(extension);

                    var qualityModel = new QualityModel { Quality = quality };
                    reasonByPath.TryGetValue(filePath, out var newReason);
                    var bookFile = new BookFile
                    {
                        Path = filePath,
                        Size = fileInfo.Length,
                        Modified = fileInfo.LastWriteTime,
                        DateAdded = DateTime.UtcNow,
                        EditionId = 0, // This marks it as unmapped
                        Quality = qualityModel,
                        MediaInfo = new MediaInfoModel(), // Initialize MediaInfo to prevent null constraint errors
                        MediaType = BookFile.DetermineMediaType(qualityModel), // Set correct MediaType based on file extension/quality
                        MatchDetails = newReason
                    };

                    TryHydrateUnmappedMetadata(bookFile, fileInfo);

                    _logger.Debug("[DISK-SCAN] Queueing new unmapped file: {0} with quality: {1}", filePath, quality);
                    newUnmappedFiles.Add(bookFile);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[DISK-SCAN] Failed to prepare unmapped file: {0}", filePath);
                }
            }

            if (updatedUnmappedFiles.Any())
            {
                try
                {
                    _mediaFileService.Update(updatedUnmappedFiles);
                    _logger.Debug("[DISK-SCAN] Hydrated metadata for {0} existing unmapped files", updatedUnmappedFiles.Count);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[DISK-SCAN] Failed to hydrate metadata for {0} existing unmapped files", updatedUnmappedFiles.Count);
                }
            }

            if (!newUnmappedFiles.Any())
            {
                return;
            }

            try
            {
                _mediaFileService.AddMany(newUnmappedFiles);
                _logger.Debug("[DISK-SCAN] Added {0} new unmapped files to database", newUnmappedFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[DISK-SCAN] Batch save of {0} unmapped files failed; falling back to per-file save", newUnmappedFiles.Count);

                foreach (var bookFile in newUnmappedFiles)
                {
                    try
                    {
                        if (_mediaFileService.GetFileWithPath(bookFile.Path) == null)
                        {
                            bookFile.Id = 0;
                            _mediaFileService.Add(bookFile);
                        }
                    }
                    catch (Exception innerEx)
                    {
                        _logger.Error(innerEx, "[DISK-SCAN] Failed to save unmapped file: {0}", bookFile.Path);
                    }
                }
            }
        }

        private void CompletedScanning(Author author)
        {
            _logger.Info("Completed scanning disk for {0}", author.Name);
            _eventAggregator.PublishEvent(new AuthorScannedEvent(author));
        }

        private void CheckForPauseAndWait(ref CancellationToken cancellationToken)
        {
            // Get command ID from ProgressMessageContext
            var commandId = ProgressMessageContext.CommandModel?.Id;

            if (commandId.HasValue)
            {
                var command = _commandQueueManager.Get(commandId.Value);

                if (command != null && command.Status == CommandStatus.Paused)
                {
                    _logger.Info("Scan paused, waiting for resume...");

                    while (command != null && command.Status == CommandStatus.Paused)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Thread.Sleep(1000);
                        command = _commandQueueManager.Get(commandId.Value);

                        if (command?.Status == CommandStatus.Started)
                        {
                            _logger.Info("Scan resumed, continuing...");
                            return;
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Execute(RescanFoldersCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        public void Execute(RescanFoldersCommand message, CancellationToken cancellationToken)
        {
            _logger.Debug("[DISK-SCAN] RescanFoldersCommand received - Paths: {0}, UnmappedFilesScope: {1}",
                message.Paths?.Count ?? 0,
                message.UnmappedFiles?.Scope ?? "none");
            LogMemorySnapshot("RescanFoldersCommand received (folders={0}, paths={1}, unmappedScope={2})",
                message.Folders?.Count ?? 0,
                message.Paths?.Count ?? 0,
                message.UnmappedFiles?.Scope ?? "none");

            if (message.UnmappedFiles != null)
            {
                var paths = ResolveUnmappedFilePaths(message.UnmappedFiles, message.MediaType);
                LogMemorySnapshot("RescanFoldersCommand after resolving unmapped paths ({0} paths, scope={1}, mediaType={2})",
                    paths.Count,
                    message.UnmappedFiles.Scope,
                    message.MediaType ?? "all");
                if (!paths.Any())
                {
                    _logger.Debug("[DISK-SCAN] No currently unmapped files matched scope '{0}' and mediaType '{1}'",
                        message.UnmappedFiles.Scope,
                        message.MediaType ?? "all");
                    return;
                }

                ScanSpecificFiles(
                    paths,
                    message.Filter,
                    message.IsInitialImport,
                    cancellationToken);
                return;
            }

            if (message.Paths != null && message.Paths.Any())
            {
                // Specific paths from unmapped files
                LogMemorySnapshot("RescanFoldersCommand before explicit path scan ({0} paths)", message.Paths.Count);
                ScanSpecificFiles(
                    message.Paths,
                    message.Filter,
                    message.IsInitialImport,
                    cancellationToken);
            }
            else
            {
                // Normal folder scan
                Scan(message.Folders, message.Filter, message.AuthorIds, message.IsInitialImport, cancellationToken);
            }
        }

        private List<string> ResolveUnmappedFilePaths(UnmappedFilesSelection selection, string mediaType)
        {
            return UnmappedFileSelectionResolver.ResolvePaths(
                _mediaFileService,
                selection,
                mediaType,
                _logger,
                "[DISK-SCAN]");
        }

        // IDiskScanService implementation
        public void Scan(Author author)
        {
            _logger.Debug("[DISK-SCAN] Scanning author: {0}", author.Name);

            var folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(author.Path))
            {
                folders.Add(author.Path);
            }
            if (!string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
            {
                folders.Add(author.AudiobookRootFolderPath);
            }
            if (!string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
            {
                folders.Add(author.EbookRootFolderPath);
            }

            if (folders.Any())
            {
                ProcessFolders(folders.Distinct().ToList(), CancellationToken.None);
            }
        }

        public void ScanRootFolder(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default)
        {
            _logger.Debug("[DISK-SCAN] Scanning root folder: {0} (mode: {1})", path, mode);
            ProcessFolders(new List<string> { path }, cancellationToken);
        }

        public async Task ScanRootFolderAsync(string path, AuthorScanMode mode = AuthorScanMode.All, CancellationToken cancellationToken = default)
        {
            _logger.Debug("[DISK-SCAN] Scanning root folder async: {0} (mode: {1})", path, mode);
            await Task.Run(() => ProcessFolders(new List<string> { path }, cancellationToken), cancellationToken);
        }

        private void ProcessFolders(List<string> folders, CancellationToken cancellationToken)
        {
            // This is just a wrapper around Scan for now
            Scan(folders, FilterFilesType.Known, null, false, cancellationToken);
        }
    }
}
