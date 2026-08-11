using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Npgsql;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.MediaFiles; // for IMoveBookFiles
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// ImportApprovedBooks is responsible ONLY for importing approved files to existing library items.
    /// It does NOT create authors, books, or series. Files must be matched to existing items.
    /// </summary>
    public class ImportApprovedBooks : IImportApprovedBooks
    {
        private const string ConversionArtifactManifestFileName = "conversion-artifact.json";
        private static readonly string[] SourceCoverBaseNames = { "cover", "front", "folder", "album", "albumart" };
        private static readonly string[] SourceCoverExtensions = { ".jpg", ".jpeg", ".png" };
        private static readonly object ConversionSemaphoreLock = new object();
        private static SemaphoreSlim _conversionSemaphore = new SemaphoreSlim(1, 1);
        private static int _conversionSemaphoreLimit = 1;

        private sealed class PendingFileCommit
        {
            public BookFile BookFile { get; init; }
            public LocalBook LocalBook { get; init; }
            public Author Author { get; init; }
            public string SourcePath { get; init; }
            public string DestinationPath { get; set; }
            public bool CopyOnly { get; init; }
            public bool DatabaseCommitted { get; set; }
            public CalibreBookFileImport CalibreImport { get; init; }
            public List<(BookFile OldFile, string BackupPath)> StagedReplacements { get; init; } = new();
            public List<BookFile> DatabaseRowsToReplace { get; } = new();
        }

        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IExtraService _extraService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IHistoryService _historyService;
        private readonly NzbDrone.Core.Download.History.IDownloadHistoryService _downloadHistoryService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly ISeriesService _seriesService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IM4bConversionService _m4bConversionService;
        private readonly IConversionTrackingService _conversionTrackingService;
        private readonly IConversionJobService _conversionJobService;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly IContainmentValidator _containmentValidator;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly ICustomFormatCalculationService _customFormatCalculationService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IUpgradeMediaFiles _upgradeMediaFiles;
        private readonly Logger _logger;

        public ImportApprovedBooks(
            IMediaFileService mediaFileService,
            IMetadataTagService metadataTagService,
            IMediaInfoExtractor mediaInfoExtractor,
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IRecycleBinProvider recycleBinProvider,
            IExtraService extraService,
            IMoveBookFiles bookFileMover,
            IHistoryService historyService,
            NzbDrone.Core.Download.History.IDownloadHistoryService downloadHistoryService,
            IEventAggregator eventAggregator,
            IManageCommandQueue commandQueueManager,
            ISeriesBookLinkService seriesBookLinkService,
            ISeriesService seriesService,
            IQualityProfileService qualityProfileService,
            IM4bConversionService m4bConversionService,
            Logger logger,
            IConversionTrackingService conversionTrackingService = null,
            IDiskProvider diskProvider = null,
            IConfigService configService = null,
            IContainmentValidator containmentValidator = null,
            IMapCoversToLocal coverMapper = null,
            ICustomFormatCalculationService customFormatCalculationService = null,
            IConversionJobService conversionJobService = null,
            IRootFolderService rootFolderService = null,
            IUpgradeMediaFiles upgradeMediaFiles = null)
        {
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _mediaInfoExtractor = mediaInfoExtractor;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _recycleBinProvider = recycleBinProvider;
            _extraService = extraService;
            _bookFileMover = bookFileMover;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _seriesBookLinkService = seriesBookLinkService;
            _seriesService = seriesService;
            _qualityProfileService = qualityProfileService;
            _m4bConversionService = m4bConversionService;
            _conversionTrackingService = conversionTrackingService;
            _conversionJobService = conversionJobService;
            _diskProvider = diskProvider;
            _configService = configService;
            _containmentValidator = containmentValidator;
            _coverMapper = coverMapper;
            _customFormatCalculationService = customFormatCalculationService;
            _rootFolderService = rootFolderService;
            _upgradeMediaFiles = upgradeMediaFiles;
            _logger = logger;
        }

        public List<ImportResult> Import(
            List<ImportDecision<LocalBook>> decisions,
            bool replaceExisting,
            DownloadClientItem downloadClientItem = null,
            ImportMode importMode = ImportMode.Auto,
            CancellationToken cancellationToken = default)
        {
            _logger.Debug("[CLEAN-IMPORT] Starting import of {0} files", decisions.Count);
            var downloadForced = downloadClientItem?.DownloadForced == true;
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("[MEMORY] Clean import start ({0} decisions): {1}", decisions.Count, MemorySnapshot.CaptureDetailed());
            }

            var importResults = new List<ImportResult>();
            var importedBookEventContexts = new List<(Author Author, Book Book, BookFile File)>();
            var bookFileConvertedEvents = new List<BookFileConvertedEvent>();

            // Filter to only approved decisions
            var approvedDecisions = decisions.Where(c => c.Approved).ToList();
            _logger.Debug("[CLEAN-IMPORT] {0} approved decisions out of {1} total", approvedDecisions.Count, decisions.Count);

            // Add rejected decisions to results
            foreach (var decision in decisions.Where(c => !c.Approved))
            {
                importResults.Add(new ImportResult(decision, "Rejected: " + string.Join(", ", decision.Rejections.Select(r => r.Reason))));
            }

            // Separate matched and unmatched files
            var matchedDecisions = approvedDecisions.Where(d => d.Item?.Book?.Id > 0).ToList();
            var unmatchedDecisions = approvedDecisions.Where(d => d.Item?.Book?.Id == 0 || d.Item?.Book == null).ToList();

                // Enforce QualityProfile gating before any moves: if not allowed, reject and do not import.
                // Manual imports are an explicit user choice and must not be silently gated.
                var gatedMatched = new List<ImportDecision<LocalBook>>();
                var authorCache = new Dictionary<int, Author>();
                foreach (var md in matchedDecisions)
                {
                    var local = md.Item;
                    if (local?.IsManualImport == true || downloadForced)
                    {
                        _logger.Debug("[QUALITY-GATE] Skipping quality gating for explicit user import/grab: '{0}'", local.Path);
                        gatedMatched.Add(md);
                        continue;
                    }

                    try
                    {
                        Author author = null;
                        if (local?.Author != null)
                        {
                            author = local.Author;
                        }
                        else if (local?.Book?.AuthorId > 0)
                        {
                            if (!authorCache.TryGetValue(local.Book.AuthorId, out author))
                            {
                                author = _authorService.GetAuthor(local.Book.AuthorId);
                                authorCache[local.Book.AuthorId] = author;
                            }
                        }

                        if (author == null)
                        {
                            md.Reject(new Rejection("Author not found"));
                            importResults.Add(new ImportResult(md, "Author not found"));
                            continue;
                        }

                        var quality = local?.Quality?.Quality ?? Qualities.Quality.Unknown;
                        var profile = author.GetQualityProfileForQuality(quality);
                        if (profile == null)
                        {
                            gatedMatched.Add(md); // no profile → do not gate
                            continue;
                        }

                        var allowed = profile.Items.Any(item => item.Allowed && item.GetQualities().Any(x => x.Id == quality.Id));
                        if (!allowed)
                        {
                            var reason = $"Quality '{quality.Name}' not allowed by profile '{profile.Name}'";

                            // Categorised so completed-download handling can tell "the payload is a
                            // format this profile never wanted" (a deterministic, re-grabbable
                            // mismatch) apart from transient or unknown import failures. Read back
                            // via Rejection.IsQualityFilter — never by matching this message.
                            md.Reject(new Rejection(reason, RejectionType.Permanent, canBypass: false, category: "Quality", severity: 3));
                            importResults.Add(new ImportResult(md, reason));
                            _logger.Debug("[QUALITY-GATE] Rejecting '{0}' — {1}", local?.Path, reason);
                        }
                        else
                        {
                            gatedMatched.Add(md);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[QUALITY-GATE] Error evaluating quality for '{0}'", local?.Path);
                        md.Reject(new Rejection("Quality not allowed by profile"));
                        importResults.Add(new ImportResult(md, "Quality not allowed by profile"));
                    }
                }
                matchedDecisions = gatedMatched;

                // Unmatched (approved-but-not-associated) decisions should never be routed through ImportApprovedBooks.
                // This class is responsible for importing files that are already matched to existing library items.
                if (unmatchedDecisions.Any())
                {
                    _logger.Debug("[CLEAN-IMPORT] {0} approved decisions were missing book matches; skipping", unmatchedDecisions.Count);
                    foreach (var decision in unmatchedDecisions)
                    {
                        importResults.Add(new ImportResult(decision, "File was not matched to an existing book"));
                    }
                }

                var hasRejectedTrackedDownloadDecisions = downloadClientItem != null && decisions.Any(c => !c.Approved);

                // Group matched files by book
                var bookGroups = matchedDecisions
                .GroupBy(d => d.Item.Book.Id)
                .ToList();

            if (!bookGroups.Any())
            {
                _logger.Debug("[CLEAN-IMPORT] No files matched to existing books after orchestrator processing");
                return importResults;
            }

            // Readarr-conformant history tracking: publish TrackImportedEvent per imported file.
            // This powers Activity -> History "Book Imported" rows for both download imports and manual imports.
            var trackImportedEvents = new List<TrackImportedEvent>();

            foreach (var bookGroup in bookGroups)
            {
                var __bookGroupSw = Stopwatch.StartNew();
                var __route = "existing";
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("[CLEAN-IMPORT] Import cancelled");
                    break;
                }

                var bookDecisions = bookGroup.ToList();
                var book = bookDecisions.First().Item.Book;
                var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);

                if (author == null)
                {
                    _logger.Error("[CLEAN-IMPORT] Could not find author for book {0} (AuthorId: {1})", book.Title, book.AuthorId);
                    foreach (var decision in bookDecisions)
                    {
                        importResults.Add(new ImportResult(decision, "Author not found"));
                    }
                    continue;
                }

                bookDecisions = SelectSingleEbookAlternative(bookDecisions, author, importResults);
                if (!bookDecisions.Any())
                {
                    _logger.Debug("[EBOOK-DUPLICATE-FORMAT] No ebook import candidates remain for book: {0}", book.Title);
                    continue;
                }

                _logger.Debug("[CLEAN-IMPORT] Importing {0} files for book: {1} by {2}",
                    bookDecisions.Count, book.Title, author.Name);

                var conversionWorkFolder = (string)null;

                // Only audiobook files should carry multipart numbering. Alternate ebook formats
                // under the same edition remain independent single-part files.
                PartAssignmentHelper.NormalizeLocalBooksByEdition(bookDecisions.Select(d => d.Item).ToList());

                // Check if we need to create a new book instance for this batch
                // (when quality profile doesn't allow upgrades)
                var firstDecision = bookDecisions.First();
                var localBook = firstDecision.Item;
                var qualityProfile = author.GetQualityProfileForQuality(localBook.Quality.Quality);

                // Check if any existing files would be replaced
                var existingFiles = _mediaFileService.GetFilesByBook(book.Id);

                // Log the existing files situation
                _logger.Debug("[UPGRADE-CHECK] Book '{0}' (ID: {1}) has {2} existing files",
                    book.Title, book.Id, existingFiles.Count);
                if (existingFiles.Any())
                {
                    foreach (var file in existingFiles)
                    {
                        _logger.Debug("[UPGRADE-CHECK]   Existing file: {0} (Quality: {1})",
                            file.Path, file.Quality?.Quality?.Name ?? "Unknown");
                    }
                }

                // CRITICAL BUG FIX: Only consider it a replacement if there are ACTUAL files to replace
                // If the book is "Missing" (no files), we should import to it, not create a duplicate
                var filesToReplace = existingFiles.Any() && replaceExisting;
                var manualReplaceExisting = replaceExisting && (downloadForced || bookDecisions.Any(d => d.Item?.IsManualImport == true));

                _logger.Debug("[UPGRADE-CHECK] replaceExisting={0}, existingFiles.Any()={1}, filesToReplace={2}, UpgradeAllowed={3}",
                    replaceExisting, existingFiles.Any(), filesToReplace, qualityProfile?.UpgradeAllowed ?? true);

                    var conversionResult = ConvertBookGroupIfNeeded(bookDecisions, book, author, downloadClientItem, replaceExisting, hasRejectedTrackedDownloadDecisions, downloadForced);
                    conversionWorkFolder = conversionResult.WorkFolder;
                    if (!conversionResult.Failed && conversionResult.Decisions == null)
                    {
                        foreach (var decision in bookDecisions)
                        {
                            importResults.Add(new ImportResult(decision, ImportResultType.Pending));
                        }

                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup deferred route=conversion-job title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);
                        continue;
                    }

                    if (conversionResult.Failed)
                    {
                        foreach (var decision in bookDecisions)
                        {
                            importResults.Add(new ImportResult(decision, conversionResult.Error));
                        }

                        CleanupConversionWorkFolder(conversionWorkFolder);
                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup skipped route=conversion-failed title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);
                        continue;
                    }

                    bookDecisions = conversionResult.Decisions;
                    var conversionDownloadId = bookDecisions.Any(d => d.Item?.IsGeneratedConversion == true) ? downloadClientItem?.DownloadId : null;

                    Book newBookInstance = null;
                    Edition newEdition = null;
                    var createdNewBookInstance = false;

                // A user-selected edition gets first crack in matching. If retained evidence still selects
                // another edition, preserve both truths by routing the import to a separate Book instance.
                // The shared policy covers the normal GUI pin (AnyEditionOk=false) and the local ManualAdd
                // preservation marker; never let automatic import clear either through SetMonitored().
                if (newBookInstance == null)
                {
                    var allEditionsForBook = _editionService.GetEditionsByBook(book.Id);
                    var firstMismatch = bookDecisions.FirstOrDefault(decision =>
                        decision?.Item?.Edition?.Id > 0 &&
                        EditionPinPolicy.FindConflictingProtectedEdition(book, allEditionsForBook, decision.Item.Edition.Id) != null);

                    if (firstMismatch != null)
                    {
                        var protectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(
                            book,
                            allEditionsForBook,
                            firstMismatch.Item.Edition.Id);

                        if (protectedEdition != null)
                        {
                            _logger.Debug("[MANUAL-EDITION-PROTECTION] User pinned edition {0} ('{1}') but file(s) matched to different edition(s). Creating clone to preserve user's selection.",
                                protectedEdition.Id, protectedEdition.Title);

                            var createResult = CreateNewBookInstanceForBatch(firstMismatch, book, author, anyEditionOk: true);
                            if (createResult?.NewBook == null || createResult.NewEdition == null)
                            {
                                foreach (var decision in bookDecisions)
                                {
                                    importResults.Add(new ImportResult(decision, "PINNED_EDITION_DESTINATION_CONFLICT"));
                                }

                                CleanupConversionWorkFolder(conversionWorkFolder);
                                __bookGroupSw.Stop();
                                _logger.Warn("[MANUAL-EDITION-PROTECTION] Could not create a safe destination for BookId={0}; import was not applied", book.Id);
                                continue;
                            }

                            newBookInstance = createResult.NewBook;
                            newEdition = createResult.NewEdition;
                            createdNewBookInstance = true;
                            _logger.Debug("[MANUAL-EDITION-PROTECTION] Created new book instance ID: {0} for files not matching pinned edition",
                                newBookInstance.Id);
                            __route = "manual-edition-protection";
                        }
                    }
                }

                if (qualityProfile != null && filesToReplace && !qualityProfile.UpgradeAllowed && !manualReplaceExisting)
                {
                    // Check if any of the files in this batch already exist at their exact paths
                    var anyFilesAlreadyImported = false;
                    foreach (var decision in bookDecisions)
                    {
                        var existingFileAtPath = _mediaFileService.GetFileWithPath(decision.Item.Path);
                        if (existingFileAtPath != null && existingFileAtPath.EditionId > 0)
                        {
                            anyFilesAlreadyImported = true;
                            break;
                        }
                    }
                    
                    if (anyFilesAlreadyImported)
                    {
                        // These exact files are already imported - skip the batch
                        _logger.Debug("[ALREADY-IMPORTED] Files already imported for book {0}, skipping re-import of {1} files",
                            book.Id, bookDecisions.Count);
                        var skipReason = $"Files already imported for book {book.Id}; converted M4B was not imported.";
                        AddConvertedWithoutImportedHistory(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, skipReason);
                        _conversionTrackingService?.Fail(conversionDownloadId, skipReason);
                        CleanupConversionWorkFolder(conversionWorkFolder);
                        __bookGroupSw.Stop();
                        _logger.Debug("[BOOK-TIMING] BookGroup skipped route=already-imported title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);
                        continue; // Skip to next batch
                    }
                    else
                    {
                        var additionalCopyCollisionReason = GetAdditionalCopyPathCollisionReason(bookDecisions, book, author);
                        if (!string.IsNullOrWhiteSpace(additionalCopyCollisionReason))
                        {
                            _logger.Warn("[ADDITIONAL-COPY] {0}", additionalCopyCollisionReason);
                            AddConvertedWithoutImportedHistory(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, additionalCopyCollisionReason);
                            _conversionTrackingService?.Fail(conversionDownloadId, additionalCopyCollisionReason);
                            CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, additionalCopyCollisionReason);
                            __bookGroupSw.Stop();
                            _logger.Debug("[BOOK-TIMING] BookGroup skipped route=additional-copy-collision title='{0}' elapsed={1}ms", book.Title, __bookGroupSw.ElapsedMilliseconds);

                            foreach (var decision in bookDecisions)
                            {
                                importResults.Add(new ImportResult(decision, additionalCopyCollisionReason));
                            }

                            continue;
                        }

                        // These are NEW files for the same book - create a new book instance for multiple copies
                        _logger.Debug("[ADDITIONAL-COPY] Creating new book instance for additional physical copy of '{0}'",
                            book.Title);
                        var createResult = CreateNewBookInstanceForBatch(bookDecisions.First(), book, author, anyEditionOk: true);
                            if (createResult.NewBook != null)
                            {
                                newBookInstance = createResult.NewBook;
                                newEdition = createResult.NewEdition;
                                createdNewBookInstance = true;
                                _logger.Debug("[ADDITIONAL-COPY] Created new book instance with ID: {0} for additional copy",
                                    newBookInstance.Id);
                                __route = "additional-copy";
                        }
                    }
                }

                    if (conversionDownloadId.IsNotNullOrWhiteSpace())
                    {
                        _conversionTrackingService?.Progress(conversionDownloadId, 99m, "Importing M4B");
                    }

                    // Collect all BookFile objects for this book to batch insert them
                    var bookFilesToAdd = new List<BookFile>();
                    var pendingFileCommits = new List<PendingFileCommit>();
                    var bookImportResults = new List<ImportResult>();
                    // Track all successfully imported book files (including ones already present in the DB)
                    // so BookImportedEvent accurately reflects the work performed.
                    var importedBookFilesForBook = new List<BookFile>();

                _logger.Debug("[BATCH-DEBUG] Processing {0} files for book ID {1}, using {2}",
                    bookDecisions.Count,
                    newBookInstance?.Id ?? book.Id,
                    newBookInstance != null ? "new book instance" : "existing book");

                // Process each file in the book group
                foreach (var decision in bookDecisions)
                {
                    var fileStopwatch = Stopwatch.StartNew();
                    try
                    {
                        ImportResult result;
                        BookFile bookFile;
                        PendingFileCommit pendingFileCommit;
                        Book importedTargetBook;

                        if (newBookInstance != null)
                        {
                            // Import to the new book instance we created for this batch
                            var fileLocalBook = decision.Item;
                            fileLocalBook.Book = newBookInstance;
                            fileLocalBook.Edition = newEdition;
                            importedTargetBook = newBookInstance;
                            (result, bookFile) = ImportFile(decision, newBookInstance, author, false, downloadClientItem, importMode, downloadForced, out pendingFileCommit);
                        }
                        else
                        {
                            // Normal import
                            importedTargetBook = book;
                            (result, bookFile) = ImportFile(decision, book, author, replaceExisting, downloadClientItem, importMode, downloadForced, out pendingFileCommit);
                        }

                            if (result.Result == ImportResultType.Imported && bookFile != null)
                            {
                                if (pendingFileCommit != null && bookFile.Id == 0)
                                {
                                    pendingFileCommits.Add(pendingFileCommit);
                                }

                                importedBookFilesForBook.Add(bookFile);
                                if (importedTargetBook != null)
                                {
                                    importedBookEventContexts.Add((author, importedTargetBook, bookFile));
                                }

                                // Ensure TrackImportedEvent has the author relationship required by HistoryService.
                                decision.Item.Author ??= author;

                                var convertedEvent = CreateGeneratedConversionEvent(decision.Item, bookFile, author, importedTargetBook, downloadClientItem);
                                if (convertedEvent != null)
                                {
                                    bookFileConvertedEvents.Add(convertedEvent);
                                }

                            // TrackImportedEvent needs the BookFile object reference; its Id will be populated after AddMany.
                            // Use "!ExistingFile" to match Readarr behavior: existing library files don't create history rows.
                            trackImportedEvents.Add(new TrackImportedEvent(
                                decision.Item,
                                bookFile,
                                new List<BookFile>(),
                                !decision.Item.ExistingFile || decision.Item.IsManualImport,
                                downloadClientItem));

                            // Only add to batch insert if it's a new file (ID == 0)
                            // Orphaned files that were relinked already have an ID and were updated
                            if (bookFile.Id == 0)
                            {
                                // Collect the file for batch insertion
                                bookFilesToAdd.Add(bookFile);
                            }
                        }

                        importResults.Add(result);
                        bookImportResults.Add(result);
                        fileStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] File import for '{0}' took {1}ms",
                            Path.GetFileName(decision.Item.Path), fileStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        fileStopwatch.Stop();
                        _logger.Error(ex, "[CLEAN-IMPORT] Failed to import file: {0} after {1}ms",
                            decision.Item.Path, fileStopwatch.ElapsedMilliseconds);
                        var failedResult = new ImportResult(decision, "Import failed: " + ex.Message);
                        importResults.Add(failedResult);
                        bookImportResults.Add(failedResult);
                    }
                }

                // Batch insert all files for this book at once
                if (bookFilesToAdd.Any())
                {
                    _logger.Debug("[BATCH-DEBUG] Collected {0} files to insert", bookFilesToAdd.Count);
                    for (int i = 0; i < Math.Min(3, bookFilesToAdd.Count); i++)
                    {
                        var f = bookFilesToAdd[i];
                        _logger.Debug("[BATCH-DEBUG] File {0}: EditionId={1}, Path={2}",
                            i, f.EditionId, Path.GetFileName(f.Path));
                    }

                        var batchStopwatch = Stopwatch.StartNew();
                        _logger.Debug("[BATCH-INSERT] Adding {0} files for book '{1}' to database", bookFilesToAdd.Count, book.Title);
                        try
                        {
                            CommitPreparedBookFiles(bookFilesToAdd, pendingFileCommits);
                        }
                        catch (SqliteException ex) when (IsBookFilesPathUniqueViolation(ex))
                        {
                            RetryPreparedBookFilesAfterPathConflict(bookFilesToAdd, pendingFileCommits, ex);
                        }
                        catch (PostgresException ex) when (IsBookFilesPathUniqueViolation(ex))
                        {
                            RetryPreparedBookFilesAfterPathConflict(bookFilesToAdd, pendingFileCommits, ex);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                RollbackPendingFileCommits(pendingFileCommits);
                            }
                            catch (Exception rollbackException)
                            {
                                throw new AggregateException("BookFile persistence failed and compensation also failed.", ex, rollbackException);
                            }

                            throw;
                        }

                        FinalizePendingFileCommits(pendingFileCommits);
                        WriteTagsForCommittedFiles(pendingFileCommits);
                        batchStopwatch.Stop();
                        _logger.Debug("[BATCH-INSERT] Successfully inserted {0} files in {1}ms", bookFilesToAdd.Count, batchStopwatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        _logger.Warn("[BATCH-DEBUG] No files collected for batch insertion for book '{0}'", book.Title);
                    }
    
                    // If we created a new book instance for this batch but ended up importing nothing into it,
                    // clean it up to avoid accumulating orphan duplicates on retries.
                    if (createdNewBookInstance && newBookInstance != null)
                    {
                        CleanupNewBookInstanceIfEmpty(createdNewBookInstance, newBookInstance, newEdition, importedBookFilesForBook);
                    }

                    __bookGroupSw.Stop();
                    _logger.Debug("[BOOK-TIMING] BookGroup route={0} bookId={1} title='{2}' files={3} elapsed={4}ms",
                    __route,
                    newBookInstance?.Id ?? book.Id,
                    book.Title,
                    bookDecisions.Count,
                    __bookGroupSw.ElapsedMilliseconds);

                    var retainConversionWorkFolder = false;
                    if (conversionDownloadId.IsNotNullOrWhiteSpace())
                    {
                        var generatedResults = bookImportResults
                            .Where(r => r.ImportDecision?.Item?.IsGeneratedConversion == true)
                            .ToList();
                        var generatedImport = generatedResults.FirstOrDefault(r => r.Result == ImportResultType.Imported);

                        if (generatedImport != null)
                        {
                            _conversionTrackingService?.Progress(conversionDownloadId, 100m, "Converted M4B imported");
                            _conversionJobService?.Complete(conversionDownloadId);
                            _conversionTrackingService?.Complete(conversionDownloadId);
                        }
                        else
                        {
                            var error = generatedResults
                                .SelectMany(r => r.Errors)
                                .FirstOrDefault(e => e.IsNotNullOrWhiteSpace()) ?? "Converted M4B was not imported.";

                            AddConvertedWithoutImportedHistory(bookFileConvertedEvents, bookDecisions, author, book, downloadClientItem, error);
                            _conversionJobService?.Fail(conversionDownloadId, error);
                            _conversionTrackingService?.Fail(conversionDownloadId, error);
                            retainConversionWorkFolder = true;
                        }
                    }

                    if (retainConversionWorkFolder)
                    {
                        CleanupOrRetainConversionWorkFolder(conversionWorkFolder, bookDecisions, "Converted M4B was not imported.");
                    }
                    else
                    {
                        CleanupConversionWorkFolder(conversionWorkFolder);
                    }
            }

            // Unmapped files are now handled at the beginning through ImportOrchestratorV2

            // Now that BookFiles have been inserted and IDs generated, publish per-file import events.
            // These power Activity -> History and download tracking.
            if (bookFileConvertedEvents.Any())
            {
                foreach (var bookFileConvertedEvent in bookFileConvertedEvents)
                {
                    _eventAggregator.PublishEvent(bookFileConvertedEvent);
                }
            }

            if (trackImportedEvents.Any())
            {
                foreach (var trackImportedEvent in trackImportedEvents)
                {
                    _eventAggregator.PublishEvent(trackImportedEvent);
                }
            }

            // Publish events for imported files
            if (importedBookEventContexts.Any())
            {
                var eventStopwatch = Stopwatch.StartNew();
                var totalFiles = 0;

                foreach (var bookGroup in importedBookEventContexts
                    .Where(x => x.Book != null && x.Book.Id > 0 && x.File != null)
                    .GroupBy(x => x.Book.Id))
                {
                    var importedFiles = bookGroup
                        .Select(x => x.File)
                        .Where(x => x != null)
                        .DistinctBy(x => x.Id > 0 ? $"id:{x.Id}" : $"path:{x.Path}")
                        .ToList();

                    if (!importedFiles.Any())
                    {
                        continue;
                    }

                    var firstContext = bookGroup.First();
                    totalFiles += importedFiles.Count;

                    _eventAggregator.PublishEvent(new BookImportedEvent(
                        firstContext.Author,
                        firstContext.Book,
                        importedFiles,
                        new List<BookFile>(), // oldFiles were already deleted
                        downloadClientItem != null,
                        downloadClientItem));

                    // Info deliberately: default-log evidence that the import-to-notification pipeline fired.
                    _logger.Info("[NOTIFY] Published BookImportedEvent for '{0}' (BookId={1}, Files={2}, NewDownload={3})",
                        firstContext.Book.Title,
                        firstContext.Book.Id,
                        importedFiles.Count,
                        downloadClientItem != null);
                }

                eventStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] Event publishing took {0}ms for {1} files",
                    eventStopwatch.ElapsedMilliseconds, totalFiles);
            }

            _logger.Debug("[CLEAN-IMPORT] Import complete. Imported: {0}, Failed: {1}",
                importResults.Count(r => r.Result == ImportResultType.Imported),
                importResults.Count(r => r.Result != ImportResultType.Imported));
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("[MEMORY] Clean import complete ({0} decisions): {1}", decisions.Count, MemorySnapshot.CaptureDetailed());
            }

            return importResults;
        }

        internal List<ImportDecision<LocalBook>> SelectSingleEbookAlternative(
            List<ImportDecision<LocalBook>> bookDecisions,
            Author author,
            List<ImportResult> importResults)
        {
            var book = bookDecisions.FirstOrDefault()?.Item?.Book;
            if (book?.MediaType != BookMediaType.Ebook)
            {
                return bookDecisions;
            }

            var ebookCandidates = bookDecisions
                .Where(IsAutomaticEbookImportDecision)
                .ToList();

            if (ebookCandidates.Count <= 1)
            {
                return bookDecisions;
            }

            var winner = ebookCandidates.Aggregate((best, candidate) =>
                CompareEbookImportCandidate(candidate, best, author) > 0 ? candidate : best);

            var skipped = ebookCandidates.Where(d => !ReferenceEquals(d, winner)).ToList();
            var selectedQuality = winner.Item?.Quality?.Quality?.Name ?? "Unknown Text";

            foreach (var decision in skipped)
            {
                var skippedQuality = decision.Item?.Quality?.Quality?.Name ?? "Unknown Text";
                var reason = $"Skipped duplicate ebook format from the same download; selected {selectedQuality} over {skippedQuality}";
                decision.Reject(new Rejection(reason));
                importResults.Add(new ImportResult(decision, reason));
                _logger.Debug("[EBOOK-DUPLICATE-FORMAT] {0}: {1}", book.Title, reason);
            }

            return bookDecisions
                .Where(d => !skipped.Contains(d))
                .ToList();
        }

        private static bool IsAutomaticEbookImportDecision(ImportDecision<LocalBook> decision)
        {
            var localBook = decision?.Item;
            if (localBook == null || localBook.IsManualImport)
            {
                return false;
            }

            var quality = localBook.Quality?.Quality;
            if (quality == null)
            {
                return false;
            }

            return QualityMediaTypeHelper.IsEbookQuality(quality) ||
                   (quality == Qualities.Quality.Unknown && localBook.Book?.MediaType == BookMediaType.Ebook);
        }

        private static int CompareEbookImportCandidate(
            ImportDecision<LocalBook> left,
            ImportDecision<LocalBook> right,
            Author author)
        {
            var leftQuality = left.Item?.Quality ?? new QualityModel(Qualities.Quality.Unknown);
            var rightQuality = right.Item?.Quality ?? new QualityModel(Qualities.Quality.Unknown);
            var qualityProfile = author?.GetQualityProfileForQuality(leftQuality.Quality) ??
                                 author?.GetQualityProfileForQuality(rightQuality.Quality);

            var qualityCompare = qualityProfile?.Items?.Any() == true
                ? new QualityModelComparer(qualityProfile).Compare(leftQuality, rightQuality)
                : leftQuality.CompareTo(rightQuality);

            if (qualityCompare != 0)
            {
                return qualityCompare;
            }

            var sizeCompare = (left.Item?.Size ?? 0).CompareTo(right.Item?.Size ?? 0);
            if (sizeCompare != 0)
            {
                return sizeCompare;
            }

            return string.Compare(
                right.Item?.Path ?? string.Empty,
                left.Item?.Path ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private BookFileConvertedEvent CreateGeneratedConversionEvent(LocalBook localBook, BookFile importedBook, Author author, Book book, DownloadClientItem downloadClientItem, string message = null)
        {
            if (localBook?.IsGeneratedConversion != true)
            {
                return null;
            }

            return new BookFileConvertedEvent(localBook, importedBook, author, book, downloadClientItem, message);
        }

        private void AddConvertedWithoutImportedHistory(List<BookFileConvertedEvent> events, List<ImportDecision<LocalBook>> bookDecisions, Author author, Book book, DownloadClientItem downloadClientItem, string message)
        {
            var convertedLocalBook = bookDecisions
                ?.Select(d => d.Item)
                .FirstOrDefault(i => i?.IsGeneratedConversion == true);

            message = AppendRetainedConversionPath(message, convertedLocalBook);
            var convertedEvent = CreateGeneratedConversionEvent(convertedLocalBook, null, author, book, downloadClientItem, message);
            if (convertedEvent == null)
            {
                return;
            }

            var hasExisting = events.Any(e =>
                e.ImportedPath.IsNullOrWhiteSpace() &&
                string.Equals(e.ConvertedPath, convertedEvent.ConvertedPath, StringComparison.OrdinalIgnoreCase));

            if (!hasExisting)
            {
                events.Add(convertedEvent);
            }
        }

        private void CleanupNewBookInstanceIfEmpty(bool createdNewBookInstance, Book newBookInstance, Edition newEdition, List<BookFile> importedBookFilesForBook)
        {
            if (!createdNewBookInstance || newBookInstance == null)
            {
                return;
            }

            var importedToNewInstance = importedBookFilesForBook?.Any(bf =>
                bf?.EditionId > 0 && bf.EditionId == newEdition?.Id) == true;

            if (importedToNewInstance)
            {
                return;
            }

            try
            {
                _logger.Warn("[CLEAN-IMPORT] Deleting orphan book instance {0} (no files imported)", newBookInstance.Id);
                _bookService.DeleteBook(newBookInstance.Id, deleteFiles: false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[CLEAN-IMPORT] Failed deleting orphan book instance {0}", newBookInstance.Id);
            }
        }

        private (ImportResult result, BookFile bookFile) ImportFile(
            ImportDecision<LocalBook> decision,
            Book book,
            Author author,
            bool replaceExisting,
            DownloadClientItem downloadClientItem,
            ImportMode importMode,
            bool downloadForced,
            out PendingFileCommit pendingFileCommit)
        {
            pendingFileCommit = null;
            var localBook = decision.Item;
            BookFile relocateExistingFile = null;
            string relocateOriginalPath = null;
            var existingFileAlreadyAtDestination = false;
            var existingFileWasUnmapped = false;
            int? existingFileOriginalEditionId = null;
            var publishBookFileAddedEventAfterMonitoring = false;
            var writeTagsForAdoptedFileAfterMonitoring = false;
            var writeTagsForRelocatedFileAfterPersistence = false;

            try
            {
                    var totalStopwatch = Stopwatch.StartNew();

                // Ensure we have the full book object, not just a partial one
                var bookLoadStopwatch = Stopwatch.StartNew();
                if (book.Id > 0 && string.IsNullOrEmpty(book.CleanTitle))
                {
                    _logger.Debug("[EDITION-IMPORT] Loading full book object for ID {0}", book.Id);
                    book = _bookService.GetBook(book.Id);
                    if (book == null)
                    {
                        _logger.Error("[EDITION-IMPORT] Could not find book with ID {0}", decision.Item.Book.Id);
                        return (new ImportResult(decision, "Book not found in database"), null);
                    }
                }
                bookLoadStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] Book loading took {0}ms", bookLoadStopwatch.ElapsedMilliseconds);
                // Get the edition to import to
                var edition = localBook.Edition;
                if (edition == null)
                {
                    _logger.Warn("[CLEAN-IMPORT] No edition specified for file: {0}", localBook.Path);
                    return (new ImportResult(decision, "No edition specified"), null);
                }

                // Ensure we have the full edition data
                if (edition.Id > 0 && edition.Book == null)
                {
                    edition = _editionService.GetEdition(edition.Id);
                }

                // Edition selection is complete before apply. Import persists the matcher/user decision unchanged.
                var decisionEdition = edition;

                // Get the quality profile for this file type
                var qualityProfile = author.GetQualityProfileForQuality(localBook.Quality.Quality);
                if (qualityProfile == null && !downloadForced)
                {
                    _logger.Error("[CLEAN-IMPORT] No quality profile found for quality: {0}", localBook.Quality.Quality.Name);
                    return (new ImportResult(decision, "No quality profile configured for this file type"), null);
                }

                    if (qualityProfile != null)
                    {
                        _logger.Debug("[QUALITY-PROFILE] Using quality profile '{0}' (ID: {1}) for file. UpgradeAllowed: {2}",
                            qualityProfile.Name, qualityProfile.Id, qualityProfile.UpgradeAllowed);
                    }
                    else
                    {
                        _logger.Debug("[QUALITY-PROFILE] Explicit manual grab has no profile for this media type; profile gates are bypassed");
                    }

                    var customFormatRejection = GetCustomFormatImportRejectionReason(localBook, author, qualityProfile, downloadForced);
                    if (customFormatRejection.IsNotNullOrWhiteSpace())
                    {
                        _logger.Debug("[CUSTOM-FORMAT-IMPORT] Rejecting '{0}' — {1}", localBook.Path, customFormatRejection);
                        return (new ImportResult(decision, customFormatRejection), null);
                    }

                    // Check if this file already exists ANYWHERE in the database (not just for this book)
                    var existingFileAtPath = _mediaFileService.GetFileWithPath(localBook.Path);
                    if (existingFileAtPath != null)
                    {
                        // Check if this is an orphaned file (EditionId = 0) that can be relinked
                        if (existingFileAtPath.EditionId == 0)
                        {
                            _logger.Debug("[ORPHAN-RELINK] Found orphaned file at path: {0}; adopting existing row and linking to edition {1}",
                                localBook.Path, edition.Id);

                            relocateExistingFile = existingFileAtPath;
                            relocateOriginalPath = existingFileAtPath.Path;
                            existingFileOriginalEditionId = existingFileAtPath.EditionId;
                            existingFileWasUnmapped = true;
                            existingFileAlreadyAtDestination = true;
                        }
                        else
                        {
                            // Non-orphan file already tracked at this path.
                            // For explicit organize operations we must treat this as a relocation within the same book,
                            // updating the existing DB row's Path after transfer (no "delete and recreate").
                            var existingEdition = existingFileAtPath.Edition ?? _editionService.GetEdition(existingFileAtPath.EditionId);
                            if (existingEdition != null && existingEdition.BookId == book.Id)
                            {
                                relocateExistingFile = existingFileAtPath;
                                relocateOriginalPath = existingFileAtPath.Path;
                                existingFileOriginalEditionId = existingFileAtPath.EditionId;

                                if (localBook.ExistingFile)
                                {
                                    existingFileAlreadyAtDestination = true;
                                    _logger.Debug("[TRACKED-RELINK] Found existing tracked file for same book at destination; will update row in place: {0}", localBook.Path);
                                }
                                else
                                {
                                    _logger.Debug("[RELOCATE] Found existing tracked file for same book at path; will relocate and update row: {0}", localBook.Path);
                                }
                            }
                            else
                            {
                                // File already exists but is linked to a different book/edition - genuine conflict
                                return (new ImportResult(decision, "File already exists in database and is linked to a different book - import blocked"), null);
                            }
                        }
                    }

                    // Check for existing files for this specific book (for replacement logic)
                    var existingFiles = _mediaFileService.GetFilesByBook(book.Id);

                // Check if we're replacing files (upgrade from different path)
                // Check for files to replace
                _logger.Debug("[MULTI-FILE] Checking for files to replace. Existing files: {0}",
                    existingFiles.Count);

                    var manualReplaceExisting = replaceExisting && (localBook.IsManualImport || downloadForced);
                    var filesToReplace = existingFiles.Where(f =>
                        !f.Path.Equals(localBook.Path, StringComparison.OrdinalIgnoreCase) &&
                        (f.EditionId == edition.Id || manualReplaceExisting)).ToList();

                    // Relocation is not an "upgrade/replacement"; do not block or stage/delete other files.
                    if (relocateExistingFile != null)
                    {
                        filesToReplace.Clear();
                    }

                    if (filesToReplace.Any())
                    {
                        _logger.Debug("[MULTI-FILE] Found {0} files to potentially replace for edition {1}",
                            filesToReplace.Count, edition.Id);
                    foreach (var f in filesToReplace)
                    {
                        _logger.Debug("[MULTI-FILE]   File to replace: {0} (ID: {1})", f.Path, f.Id);
                    }
                }

                    if (filesToReplace.Any() && !replaceExisting)
                    {
                        _logger.Debug("[CLEAN-IMPORT] Existing files found for edition but replaceExisting is false: {0}", localBook.Path);
                        return (new ImportResult(decision, "Edition already has files"), null);
                    }

                // Note: Quality profile upgrade checks are now handled at the batch level in Import()

                // Check quality upgrade
                var worstExistingQuality = filesToReplace.Any() ?
                    filesToReplace.Min(f => f.Quality.Revision) :
                    new Revision();

                var qualityCompare = localBook.Quality.Revision.CompareTo(worstExistingQuality);
                if (filesToReplace.Any() && qualityCompare < 0 && !downloadForced)
                {
                    _logger.Debug("[CLEAN-IMPORT] This file is a lower quality revision: {0}", localBook.Path);
                    return (new ImportResult(decision, "Lower quality revision"), null);
                }

                    // Create the book file
                    // Narrator is derived from the matched edition (metadata server is canonical).
                    var isAudioMedia = BookFile.DetermineMediaType(localBook.Quality) == "audiobook";
                    string narratorName = null;

                    if (isAudioMedia && edition.NarratorNames != null && edition.NarratorNames.Any())
                    {
                        var topTwo = edition.NarratorNames.Take(2).ToList();
                        narratorName = string.Join(" + ", topTwo);
                        _logger.Debug("[CLEAN-IMPORT] Setting narrator from edition: {0}", narratorName);
                    }

                _logger.Debug("[EDITION-IMPORT] Creating BookFile record:");
                _logger.Debug("[EDITION-IMPORT]   Path: {0}", localBook.Path);
                _logger.Debug("[EDITION-IMPORT]   EditionId: {0}", edition.Id);
                _logger.Debug("[EDITION-IMPORT]   Edition Title: '{0}'", edition.Title);
                _logger.Debug("[EDITION-IMPORT]   Narrator from edition: '{0}'", narratorName ?? "NONE");
                _logger.Debug("[EDITION-IMPORT]   MediaType: {0}", BookFile.DetermineMediaType(localBook.Quality));
                _logger.Debug("[EDITION-IMPORT]   Quality: {0}", localBook.Quality);

                var durationSeconds = GetDurationSeconds(localBook);
                    var hasTrackedReleaseEvidence = localBook.DownloadClientBookInfo != null || localBook.SceneSource;
                    var isGraphicAudio = localBook.IsGraphicAudio || DetectGraphicAudioFromLocalTags(localBook);
                    var audioProductionType = localBook.AudioProductionType.IsNotNullOrWhiteSpace()
                        ? localBook.AudioProductionType
                        : isGraphicAudio ? AudioProductionConstants.DetectedDramatizedFullCastType : null;
                    var persistedNarrator = localBook.Narrator.IsNotNullOrWhiteSpace() ? localBook.Narrator : narratorName;

                    BookFile bookFile;
                    if (relocateExistingFile != null)
                    {
                        bookFile = relocateExistingFile;
                        bookFile.EditionId = edition.Id;
                        bookFile.Edition = edition;
                        if (existingFileWasUnmapped)
                        {
                            bookFile.DateAdded = DateTime.UtcNow;
                        }

                        bookFile.Part = localBook.Part > 0 ? localBook.Part : 1;
                        bookFile.PartCount = localBook.PartCount;
                        bookFile.Size = localBook.Size;
                        bookFile.Modified = localBook.Modified;
                        bookFile.Quality = localBook.Quality;
                        bookFile.MediaType = BookFile.DetermineMediaType(localBook.Quality);
                        if (hasTrackedReleaseEvidence)
                        {
                            bookFile.SceneName = localBook.SceneName;
                            bookFile.ReleaseGroup = localBook.ReleaseGroup;
                            bookFile.IndexerFlags = localBook.IndexerFlags;
                        }

                        bookFile.IsGraphicAudio = isGraphicAudio;
                        if (audioProductionType.IsNotNullOrWhiteSpace())
                        {
                            bookFile.AudioProductionType = audioProductionType;
                        }

                        if (persistedNarrator.IsNotNullOrWhiteSpace())
                        {
                            bookFile.Narrator = persistedNarrator;
                        }

                        bookFile.Author = author;
                        bookFile.AllTags = localBook.RawTags?.AllTags ?? bookFile.AllTags;
                        bookFile.DurationSeconds = durationSeconds ?? bookFile.DurationSeconds;
                        bookFile.MediaInfo = MediaDuration.ApplyToMediaInfo(bookFile.MediaInfo, durationSeconds);
                    }
                    else
                    {
                        bookFile = new BookFile
                        {
                            Path = localBook.Path,
                            CalibreId = localBook.CalibreId,
                            Part = localBook.Part > 0 ? localBook.Part : 1,
                            PartCount = localBook.PartCount,
                            Size = localBook.Size,
                            Modified = localBook.Modified,
                            DateAdded = DateTime.UtcNow,
                            Quality = localBook.Quality,
                            SceneName = localBook.SceneName,
                            ReleaseGroup = localBook.ReleaseGroup,
                            IndexerFlags = localBook.IndexerFlags,
                            MediaInfo = MediaDuration.CreateMediaInfo(durationSeconds),
                            EditionId = edition.Id,
                            Edition = edition,
                            MediaType = BookFile.DetermineMediaType(localBook.Quality),
                            IsGraphicAudio = isGraphicAudio,
                            AudioProductionType = audioProductionType,
                            Narrator = persistedNarrator,
                            Author = author,
                            AllTags = localBook.RawTags?.AllTags,
                            DurationSeconds = durationSeconds
                        };
                    }

                    var successfulMatchProvenance = localBook.MatchProvenance;
                    if (successfulMatchProvenance == null && localBook.IsManualImport)
                    {
                        successfulMatchProvenance = MatchProvenance.ManualSelection(author, book, decisionEdition);
                    }

                    if (successfulMatchProvenance != null)
                    {
                        var finalizedProvenance = successfulMatchProvenance.CloneForDestination(author, book, edition);
                        bookFile.MatchProvenance = finalizedProvenance;
                    }

                    // A successful link supersedes the why-unmapped/apply-failure scratchpad.
                    bookFile.LastMatchAttempt = null;
                    bookFile.MatchDetails = null;

                // Respect legacy Readarr behavior: if this is an existing file discovered during a library scan,
                // do not move/copy it. Only update DB state and tags.
                if (localBook.ExistingFile && !existingFileAlreadyAtDestination)
                {
                    _logger.Debug("[SCAN] Existing file detected during scan, will not move: {0}", localBook.Path);

                    // Any row at this exact path was already classified above as unmapped,
                    // same-book, or a hard conflict. Re-querying and deleting it here was
                    // unreachable for a valid scan candidate and made the invariant harder
                    // to reason about.
                    TryWriteTags(bookFile, false, "SCAN");

                    // Return the BookFile to be batch inserted by the caller
                    // No file movement, no extras import for existing files
                    _logger.Debug("[SCAN] Tracked existing file without moving: {0}", localBook.Path);
                    return (new ImportResult(decision), bookFile);
                }

                if (existingFileAlreadyAtDestination)
                {
                    var editionLinkChanged = existingFileOriginalEditionId.HasValue &&
                                             existingFileOriginalEditionId.Value != bookFile.EditionId;

                    if (existingFileWasUnmapped)
                    {
                        _logger.Debug("[ORPHAN-RELINK] Updating existing unmapped file in place without moving: {0}", localBook.Path);
                    }
                    else if (editionLinkChanged)
                    {
                        _logger.Debug("[TRACKED-RELINK] Updating existing file in place from edition {0} to edition {1}: {2}",
                            existingFileOriginalEditionId.Value,
                            bookFile.EditionId,
                            localBook.Path);
                    }
                    else
                    {
                        _logger.Debug("[TRACKED-RELINK] Refreshing existing file in place without changing its edition: {0}", localBook.Path);
                    }

                    _mediaFileService.Update(bookFile);

                    // Add publishes BookFileAddedEvent and AddMany publishes the batch-equivalent
                    // BookFilesAddedEvent for new rows. An adopted row is updated in place, so defer
                    // the equivalent single-file event until monitoring is settled, and only when the
                    // edition link changed. An unchanged rescan must not trigger added-file side effects.
                    publishBookFileAddedEventAfterMonitoring = editionLinkChanged;
                    writeTagsForAdoptedFileAfterMonitoring = true;
                }
                else
                {
                    // Handle file move/copy for new downloads
                    bool copyOnly = !localBook.IsGeneratedConversion &&
                                    (importMode == ImportMode.Copy || !ShouldMoveFile(localBook, author));
                    // Relocation of an already-tracked library file must not create duplicates; force move semantics.
                    if (relocateExistingFile != null)
                    {
                        copyOnly = false;
                    }

                    var rootFolderPath = author.GetRootFolderForQuality(localBook.Quality.Quality);
                    var rootFolder = rootFolderPath.IsNotNullOrWhiteSpace()
                        ? _rootFolderService?.GetBestRootFolder(rootFolderPath)
                        : null;
                    var importToCalibre = rootFolder?.IsCalibreLibrary == true;
                    if (importToCalibre && bookFile.Id > 0)
                    {
                        return (new ImportResult(decision, ImportResultType.Rejected, "Calibre clean import requires a new, untracked BookFile."), null);
                    }

                    if (importToCalibre && filesToReplace.Any())
                    {
                        return (new ImportResult(decision, ImportResultType.Rejected, "Calibre book replacement is not supported during clean import."), null);
                    }

                    // Generic upgrades stage filesystem replacements before transfer. Calibre
                    // replacements are rejected above because Calibre cannot atomically restore an
                    // overwritten format after a database failure.
                    var stagedReplacements = new List<(BookFile OldFile, string BackupPath)>();
                    if (!importToCalibre && filesToReplace.Any())
                    {
                        var stageStopwatch = Stopwatch.StartNew();
                        foreach (var oldFile in filesToReplace)
                        {
                            // Preserve CalibreId from the replaced file when applicable.
                            if (bookFile.CalibreId == 0 && oldFile.CalibreId != 0)
                            {
                                bookFile.CalibreId = oldFile.CalibreId;
                            }

                            if (!File.Exists(oldFile.Path))
                            {
                                continue;
                            }

                            var backupPath = GetUniqueUpgradeBackupPath(oldFile.Path);
                            try
                            {
                                File.Move(oldFile.Path, backupPath);
                                stagedReplacements.Add((oldFile, backupPath));
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "[CLEAN-IMPORT] Failed to stage existing file for upgrade: {0}", oldFile.Path);
                                RollbackStagedReplacements(stagedReplacements);
                                return (new ImportResult(decision, $"Failed to stage existing file for upgrade: {ex.Message}"), null);
                            }
                        }
                        stageStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] Old file staging took {0}ms", stageStopwatch.ElapsedMilliseconds);
                    }

                    try
                    {
                        var transferStopwatch = Stopwatch.StartNew();
                        if (importToCalibre)
                        {
                            if (_upgradeMediaFiles == null)
                            {
                                throw new InvalidOperationException("Calibre imports require the upgrade media file service.");
                            }

                            var calibreImport = _upgradeMediaFiles.PrepareCalibreImport(bookFile, rootFolder, copyOnly);
                            bookFile = calibreImport.BookFile;
                            pendingFileCommit = new PendingFileCommit
                            {
                                BookFile = bookFile,
                                LocalBook = localBook,
                                Author = author,
                                SourcePath = localBook.Path,
                                DestinationPath = bookFile.Path,
                                CopyOnly = copyOnly,
                                CalibreImport = calibreImport
                            };

                            var existingAtCalibreDestination = _mediaFileService.GetFileWithPath(bookFile.Path);
                            if (existingAtCalibreDestination?.Id > 0)
                            {
                                var conflictCommit = pendingFileCommit;
                                pendingFileCommit = null;
                                RollbackPendingFileCommit(conflictCommit);
                                return (new ImportResult(decision, ImportResultType.Rejected, "Calibre destination path is already tracked."), null);
                            }
                        }
                        else if (copyOnly)
                        {
                            bookFile = _bookFileMover.CopyBookFile(bookFile, localBook);
                        }
                        else
                        {
                            bookFile = _bookFileMover.MoveBookFile(bookFile, localBook);
                        }
                        transferStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] File transfer took {0}ms", transferStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        if (pendingFileCommit?.CalibreImport != null)
                        {
                            var failedCommit = pendingFileCommit;
                            pendingFileCommit = null;
                            try
                            {
                                RollbackPendingFileCommit(failedCommit);
                            }
                            catch (Exception rollbackException)
                            {
                                throw new AggregateException("Calibre import failed and compensation also failed.", ex, rollbackException);
                            }
                        }

                        RollbackStagedReplacements(stagedReplacements);
                        throw;
                    }

                    if (bookFile.Id == 0 && pendingFileCommit == null)
                    {
                        pendingFileCommit = new PendingFileCommit
                        {
                            BookFile = bookFile,
                            LocalBook = localBook,
                            Author = author,
                            SourcePath = localBook.Path,
                            DestinationPath = bookFile.Path,
                            CopyOnly = copyOnly,
                            StagedReplacements = stagedReplacements
                        };

                        pendingFileCommit.DatabaseRowsToReplace.AddRange(stagedReplacements.Select(replacement => replacement.OldFile));
                    }

                    // If the destination path is already tracked, either include it in the staged
                    // upgrade transaction or preserve the established stale-row adoption path.
                    if (!importToCalibre)
                    {
                        bookFile = ResolveBookFilePathConflict(bookFile, localBook, pendingFileCommit);
                    }
                    if (pendingFileCommit != null)
                    {
                        pendingFileCommit.DestinationPath = bookFile.Path;
                    }

                    if (pendingFileCommit == null || pendingFileCommit.DatabaseCommitted)
                    {
                        if (relocateExistingFile != null)
                        {
                            writeTagsForRelocatedFileAfterPersistence = true;
                        }
                        else
                        {
                            TryWriteTags(bookFile, true, "TRANSFER");
                        }
                    }

                    // Import extras (best-effort; do not fail the core import if extras post-processing fails)
                    var extrasStopwatch = Stopwatch.StartNew();
                    try
                    {
                        _extraService.ImportTrack(localBook, bookFile, copyOnly);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[CLEAN-IMPORT] Failed to import extras for: {0}", localBook.Path);
                    }
                    finally
                    {
                        extrasStopwatch.Stop();
                        _logger.Debug("[PERFORMANCE] Extras import took {0}ms", extrasStopwatch.ElapsedMilliseconds);
                    }
                }

                // Return the BookFile to be batch inserted by the caller only when it is new.
                _logger.Debug("[EDITION-IMPORT] BookFile prepared for batch insertion. Edition ID: {0}", bookFile.EditionId);

                // If we relocated an already-tracked file, update the existing DB row's Path and fields now.
                if (relocateExistingFile != null && !existingFileAlreadyAtDestination)
                {
                    try
                    {
                        _mediaFileService.Update(bookFile);
                        if (!string.IsNullOrWhiteSpace(relocateOriginalPath) && !relocateOriginalPath.PathEquals(bookFile.Path))
                        {
                            _eventAggregator.PublishEvent(new BookFileRenamedEvent(author, bookFile, relocateOriginalPath));
                        }

                        if (writeTagsForRelocatedFileAfterPersistence)
                        {
                            TryWriteTags(bookFile, false, "RELOCATE");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[RELOCATE] Failed updating DB row after relocation for: {0}", relocateOriginalPath ?? localBook.Path);
                        return (new ImportResult(decision, "Relocation succeeded on disk but failed to update database"), null);
                    }
                }

                // The imported file's edition is the persisted display/search truth for this book row.
                // Manual-pin conflicts are handled before this point by routing the import to a separate
                // book instance, so never leave "file edition" and "monitored edition" divergent here.
                if (!edition.Monitored || edition.Id != localBook.Edition.Id)
                {
                    if (edition.Id != localBook.Edition.Id)
                    {
                        _logger.Debug("[EDITION-IMPORT] Edition was switched during import, updating monitored status");
                        _logger.Debug("[EDITION-IMPORT]   Previous edition ID: {0}", localBook.Edition.Id);
                        _logger.Debug("[EDITION-IMPORT]   New edition ID: {0}", edition.Id);
                    }
                    else
                    {
                        _logger.Debug("[EDITION-IMPORT] Edition {0} has file but is not monitored, fixing monitoring status", edition.Id);
                    }

                    // SetMonitored handles everything: marks the passed edition as monitored
                    // and automatically unmonitors all other editions for this book.
                    // NOTE: The old loop was buggy - calling SetMonitored(e) for editions that should
                    // become UNmonitored would actually make them monitored (last call wins).
                    try
                    {
                        _editionService.SetMonitored(edition, false); // false = automatic during import, not manual selection
                        _logger.Debug("[EDITION-IMPORT] Monitored edition successfully updated to edition {0} which has the file", edition.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[EDITION-IMPORT] Failed to update monitored edition for book {0}", book.Id);
                    }
                }
                else
                {
                    _logger.Debug("[EDITION-IMPORT] Edition {0} is already monitored and has the file, no change needed", edition.Id);
                }

                if (publishBookFileAddedEventAfterMonitoring)
                {
                    _eventAggregator.PublishEvent(new BookFileAddedEvent(bookFile));
                }

                if (writeTagsForAdoptedFileAfterMonitoring)
                {
                    TryWriteTags(bookFile, false, "TRACKED-RELINK");
                }

                totalStopwatch.Stop();
                _logger.Debug("[PERFORMANCE] Total ImportFile took {0}ms for '{1}'",
                    totalStopwatch.ElapsedMilliseconds, Path.GetFileName(localBook.Path));
                _logger.Debug("[EDITION-IMPORT] IMPORT COMPLETE: File '{0}' imported to edition {1} with narrator '{2}'",
                    Path.GetFileName(localBook.Path), edition.Id, narratorName ?? "NONE");

                return (new ImportResult(decision), bookFile);
            }
            catch (Exception ex)
            {
                if (pendingFileCommit != null && !pendingFileCommit.DatabaseCommitted)
                {
                    var failedCommit = pendingFileCommit;
                    pendingFileCommit = null;
                    try
                    {
                        RollbackPendingFileCommit(failedCommit);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("Book import failed and compensation also failed.", ex, rollbackException);
                    }
                }

                _logger.Error(ex, "[CLEAN-IMPORT] Failed to import file: {0}", localBook.Path);
                return (new ImportResult(decision, "Import failed: " + ex.Message), null);
            }
        }

        private string GetCustomFormatImportRejectionReason(LocalBook localBook, Author author, QualityProfile qualityProfile, bool downloadForced)
        {
            if (_customFormatCalculationService == null || localBook?.ExistingFile == true || qualityProfile == null || downloadForced)
            {
                return null;
            }

            if (localBook.Author == null)
            {
                localBook.Author = author;
            }

            var formats = _customFormatCalculationService.ParseCustomFormat(localBook) ?? new List<CustomFormat>();
            var score = qualityProfile.CalculateCustomFormatScore(formats);

            if (score >= qualityProfile.MinFormatScore)
            {
                return null;
            }

            var dramatizedFormat = formats.FirstOrDefault(f =>
                f.BuiltInKey == BuiltInCustomFormats.DramatizedAudioKey ||
                f.Name == BuiltInCustomFormats.DramatizedAudioName);
            var dramatizedReason = dramatizedFormat != null
                ? $"; matched {dramatizedFormat.Name} custom format on file tags"
                : string.Empty;

            return string.Format("Custom Formats {0} have score {1} below Author profile minimum {2}{3}",
                formats.ConcatToString(),
                score,
                qualityProfile.MinFormatScore,
                dramatizedReason);
        }

            private (List<ImportDecision<LocalBook>> Decisions, string WorkFolder, bool Failed, string Error) ConvertBookGroupIfNeeded(
                List<ImportDecision<LocalBook>> bookDecisions,
                Book book,
                Author author,
                DownloadClientItem downloadClientItem,
                bool replaceExisting,
                bool hasRejectedTrackedDownloadDecisions,
                bool downloadForced)
            {
                if (downloadClientItem == null || bookDecisions == null || bookDecisions.Count == 0)
                {
                    return (bookDecisions, null, false, null);
                }

                var first = bookDecisions.First().Item;
                var sourceQuality = first?.Quality?.Quality ?? Qualities.Quality.Unknown;
                var qualityProfile = author.GetQualityProfileForQuality(sourceQuality);
                var targetQuality = QualityConversionHelper.GetPlannedConversionTarget(author, first?.Quality);
                if (targetQuality != Qualities.Quality.M4B)
                {
                    return (bookDecisions, null, false, null);
                }

                var inputFiles = bookDecisions
                    .Select(d => d.Item?.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, NaturalSortComparer.Instance)
                    .ToArray();

                if (inputFiles.Length == 0)
                {
                    return (bookDecisions, null, false, null);
                }

                if (inputFiles.All(p => Path.GetExtension(p).Equals(".m4b", StringComparison.OrdinalIgnoreCase)))
                {
                    return (bookDecisions, null, false, null);
                }

                if (hasRejectedTrackedDownloadDecisions)
                {
                    var error = "Conversion skipped because one or more files in the completed download did not match a local book. Fix the match failure, then retry import.";
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                    PublishConversionFailed(first, inputFiles, book, author, new QualityModel(Qualities.Quality.M4B), null, error, downloadClientItem);
                    return (bookDecisions, null, true, error);
                }

                var convertedQuality = new QualityModel(Qualities.Quality.M4B);
                var useDetachedJob = _conversionJobService != null && bookDecisions.All(decision => decision.Item?.IsManualImport != true);

                if (!_m4bConversionService.CanConvert(inputFiles))
                {
                    var error = "Conversion to M4B is enabled, but one or more files are not compatible with the M4B converter.";
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                    PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, error, downloadClientItem);
                    return (bookDecisions, null, true, error);
                }

                var outputName = GetSafeConvertedFileName(inputFiles.Length == 1 ? Path.GetFileNameWithoutExtension(inputFiles[0]) : book.Title);
                string workRoot = null;
                string workFolder = null;
                string outputPath = null;

                try
                {
                    var finalDestinationPath = GetConvertedImportDestinationPath(first, book, author, outputName, convertedQuality);
                    var finalDestinationFolder = Path.GetDirectoryName(finalDestinationPath);
                    if (finalDestinationFolder.IsNullOrWhiteSpace())
                    {
                        var error = "Unable to determine destination folder for converted M4B.";
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, error, downloadClientItem);
                        return (bookDecisions, null, true, error);
                    }

                    var destinationConflict = GetConversionDestinationConflictReason(finalDestinationPath, book, qualityProfile, replaceExisting, downloadForced);
                    if (destinationConflict.IsNotNullOrWhiteSpace())
                    {
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, destinationConflict);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, null, destinationConflict, downloadClientItem);
                        return (bookDecisions, null, true, destinationConflict);
                    }

                    var downloadFolderName = GetSafeConvertedFileName(downloadClientItem.DownloadId ?? Guid.NewGuid().ToString("N"));
                    workRoot = Path.Combine(finalDestinationFolder, ".chaptarr-conversions", downloadFolderName);
                    workFolder = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
                    CleanupExpiredConversionArtifacts(Path.GetDirectoryName(workRoot), TimeSpan.FromDays(7));

                    var outputFileName = Path.GetFileName(finalDestinationPath);
                    if (outputFileName.IsNullOrWhiteSpace())
                    {
                        outputFileName = outputName + ".m4b";
                    }

                    outputPath = Path.Combine(workFolder, outputFileName);
                    var tagOptions = ConversionTagProposalBuilder.BuildOptions(
                        bookDecisions.Select(d => d.Item),
                        book,
                        author,
                        first.Edition,
                        _containmentValidator,
                        _configService?.AudiobookConversionTagMode);
                    var sourceDuration = GetAudiobookConversionSourceDuration(inputFiles);
                    TryApplyMatchedEditionChapters(tagOptions, first.Edition, sourceDuration, book);
                    TryApplySourceSidecarCover(tagOptions, inputFiles, book, first.Edition);
                    ApplyConversionCoverFallback(tagOptions, book, first.Edition);
                    ConversionTagProposalBuilder.RefreshManifestJson(tagOptions, bookDecisions.Select(d => d.Item));
                    var audioBitrate = GetAudiobookConversionBitrate(inputFiles);
                    var audioChannels = GetAudiobookConversionAudioChannels();

                    if (TryFindReusableConversionArtifact(workRoot, inputFiles, convertedQuality, tagOptions, audioBitrate, audioChannels, out var reusableOutputPath))
                    {
                        _conversionTrackingService?.Start(downloadClientItem.DownloadId, Qualities.Quality.M4B.Id, Qualities.Quality.M4B.Name, "Using retained M4B");
                        _conversionTrackingService?.Progress(downloadClientItem.DownloadId, 97m, "Using retained M4B");
                        var reusableLocalBook = CreateGeneratedConversionLocalBook(first, bookDecisions, reusableOutputPath, inputFiles, convertedQuality, tagOptions);
                        _logger.Info("[CONVERSION] Reusing retained converted M4B for '{0}': {1}", book.Title, reusableOutputPath);
                        return (new List<ImportDecision<LocalBook>> { new ImportDecision<LocalBook>(reusableLocalBook) }, workRoot, false, null);
                    }

                    var existingJob = useDetachedJob ? _conversionJobService.Get(downloadClientItem.DownloadId) : null;
                    if (existingJob != null)
                    {
                        if (existingJob.Status == ConversionJobStatus.Queued ||
                            existingJob.Status == ConversionJobStatus.Converting ||
                            existingJob.Status == ConversionJobStatus.Cancelling)
                        {
                            _logger.Debug("[CONVERSION] Download {0} already has an in-flight conversion job; leaving import pending.", downloadClientItem.DownloadId);
                            return (null, existingJob.WorkRoot ?? workRoot, false, null);
                        }

                        if (existingJob.Status == ConversionJobStatus.ReadyToImport)
                        {
                            var error = "The completed conversion artifact is no longer valid for the current source files. Retry import to rebuild it.";
                            _conversionJobService.Fail(downloadClientItem.DownloadId, error);
                            PublishConversionFailed(first, inputFiles, book, author, convertedQuality, existingJob.OutputPath, error, downloadClientItem);
                            return (bookDecisions, null, true, error);
                        }

                        if (existingJob.Status == ConversionJobStatus.Failed || existingJob.Status == ConversionJobStatus.Cancelled)
                        {
                            var error = existingJob.Error ?? existingJob.Message ?? "M4B conversion did not complete.";
                            PublishConversionFailed(first, inputFiles, book, author, convertedQuality, existingJob.OutputPath, error, downloadClientItem);
                            return (bookDecisions, null, true, error);
                        }
                    }

                    var estimate = EstimateConversionWorkspace(inputFiles);
                    if (!HasEnoughConversionWorkspaceSpace(finalDestinationFolder, estimate, out var freeSpaceError))
                    {
                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, freeSpaceError);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, outputPath, freeSpaceError, downloadClientItem);
                        return (bookDecisions, null, true, freeSpaceError);
                    }

                    CleanupConversionWorkFolder(workRoot);
                    Directory.CreateDirectory(workFolder);
                    var conversionInputFiles = TryCreateProviderChapterSidecarInputs(workFolder, inputFiles, tagOptions, book);
                    ConversionTagProposalBuilder.RefreshManifestJson(tagOptions, bookDecisions.Select(d => d.Item));

                    if (useDetachedJob)
                    {
                        var sources = BuildConversionArtifactSourceSignatures(inputFiles);
                        if (sources == null || sources.Count == 0)
                        {
                            var error = "Unable to capture stable source-file identities for M4B conversion.";
                            _conversionJobService.Fail(downloadClientItem.DownloadId, error);
                            return (bookDecisions, workRoot, true, error);
                        }

                        _conversionJobService.Enqueue(new ConversionJobRequest
                        {
                            DownloadId = downloadClientItem.DownloadId,
                            BookTitle = book.Title,
                            WorkRoot = workRoot,
                            WorkFolder = workFolder,
                            OutputPath = outputPath,
                            ConversionInputFiles = conversionInputFiles.ToList(),
                            Sources = sources.Select(source => new ConversionArtifactSource
                            {
                                Path = source.Path,
                                Size = source.Size,
                                ModifiedUtcTicks = source.ModifiedUtcTicks
                            }).ToList(),
                            TargetQualityId = convertedQuality.Quality.Id,
                            TargetQualityName = convertedQuality.Quality.Name,
                            AudioBitrate = audioBitrate,
                            AudioChannels = audioChannels,
                            ExpectedSourceDurationTicks = sourceDuration.Ticks,
                            TagSignature = GetConversionTagSignature(tagOptions),
                            TagOptions = tagOptions
                        });

                        return (null, workRoot, false, null);
                    }

                    var threadPlan = GetAudiobookConversionThreadPlan(inputFiles.Length);
                    using var conversionCancellation = new CancellationTokenSource();
                    var conversionSemaphore = GetAudiobookConversionSemaphore();
                    var conversionSlotAcquired = false;
                    ConversionResult result;

                    _conversionTrackingService?.Start(downloadClientItem.DownloadId, Qualities.Quality.M4B.Id, Qualities.Quality.M4B.Name, "Waiting for M4B conversion slot");
                    _conversionTrackingService?.RegisterCancellation(downloadClientItem.DownloadId, conversionCancellation);
                    try
                    {
                        conversionSemaphore.Wait(conversionCancellation.Token);
                        conversionSlotAcquired = true;
                        _conversionTrackingService?.Progress(downloadClientItem.DownloadId, 1m, "Converting to M4B");

                        result = _m4bConversionService.ConvertToM4b(conversionInputFiles, outputPath, new ConversionOptions
                        {
                            TempDirectory = workFolder,
                            AudioBitrate = audioBitrate,
                            AudioChannels = audioChannels,
                            ExpectedSourceDuration = sourceDuration,
                            Jobs = threadPlan.ParallelFiles,
                            FfmpegThreads = threadPlan.FfmpegThreads,
                            TagOptions = tagOptions,
                            CancellationToken = conversionCancellation.Token,
                            ProgressHandler = update => _conversionTrackingService?.Progress(downloadClientItem.DownloadId, update.Progress, update.Message)
                        });
                    }
                    finally
                    {
                        if (conversionSlotAcquired)
                        {
                            conversionSemaphore.Release();
                        }
                    }

                    if (!result.Success)
                    {
                        var error = result.ErrorMessage.IsNullOrWhiteSpace() ? "M4B conversion failed" : result.ErrorMessage;
                        if (result.FailureCategory == ConversionFailureCategory.Cancelled)
                        {
                            var targetQualityName = convertedQuality.Quality.Name;
                            error = $"{targetQualityName} conversion was cancelled.";
                            _logger.Info("[CONVERSION] Cancelled {0} conversion for '{1}'. Import stopped.", targetQualityName, book.Title);
                            CleanupConversionWorkFolder(workRoot);
                            _conversionTrackingService?.Cancelled(downloadClientItem.DownloadId, error);
                            return (bookDecisions, null, true, error);
                        }

                        if (result.RetainOutputOnFailure && DestinationFileExists(outputPath))
                        {
                            WriteConversionArtifactManifest(workFolder, outputPath, inputFiles, convertedQuality, tagOptions, audioBitrate, audioChannels);
                            error = $"{error} Converted file retained at: {outputPath}";
                        }

                        _conversionTrackingService?.Fail(downloadClientItem.DownloadId, error);
                        PublishConversionFailed(first, inputFiles, book, author, convertedQuality, outputPath, error, downloadClientItem);
                        if (!result.RetainOutputOnFailure)
                        {
                            CleanupConversionWorkFolder(workRoot);
                        }

                        return (bookDecisions, null, true, error);
                    }

                    _conversionTrackingService?.Progress(downloadClientItem.DownloadId, 97m, "Finalizing M4B");

                    WriteConversionArtifactManifest(workFolder, outputPath, inputFiles, convertedQuality, tagOptions, audioBitrate, audioChannels);
                    var convertedLocalBook = CreateGeneratedConversionLocalBook(first, bookDecisions, outputPath, inputFiles, convertedQuality, tagOptions);

                    _conversionTrackingService?.Progress(downloadClientItem.DownloadId, 98m, "Preparing import");
                    _logger.Info("[CONVERSION] Converted {0} source files to M4B for '{1}': {2}", inputFiles.Length, book.Title, outputPath);

                    return (new List<ImportDecision<LocalBook>> { new ImportDecision<LocalBook>(convertedLocalBook) }, workRoot, false, null);
                }
                catch (OperationCanceledException)
                {
                    var targetQualityName = convertedQuality.Quality.Name;
                    var error = $"{targetQualityName} conversion was cancelled.";
                    _logger.Info("[CONVERSION] Cancelled {0} conversion for '{1}'. Import stopped.", targetQualityName, book.Title);
                    CleanupConversionWorkFolder(workRoot);
                    _conversionTrackingService?.Cancelled(downloadClientItem.DownloadId, error);
                    return (bookDecisions, null, true, error);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[CONVERSION] Failed converting files to M4B for '{0}'", book.Title);
                    _conversionTrackingService?.Fail(downloadClientItem.DownloadId, ex.Message);
                    PublishConversionFailed(first, inputFiles, book, author, convertedQuality, outputPath, "M4B conversion failed: " + ex.Message, downloadClientItem);
                    CleanupConversionWorkFolder(workRoot ?? workFolder);
                    return (bookDecisions, null, true, "M4B conversion failed: " + ex.Message);
                }
            }

            private void TryApplyMatchedEditionChapters(ConversionTagOptions tagOptions, Edition edition, TimeSpan sourceDuration, Book book)
            {
                if (tagOptions == null || edition?.Chapters == null || edition.Chapters.Count < 2)
                {
                    return;
                }

                var chapters = edition.Chapters
                    .Where(chapter => chapter != null &&
                                      chapter.StartOffsetMs >= 0 &&
                                      chapter.Title.IsNotNullOrWhiteSpace())
                    .GroupBy(chapter => chapter.StartOffsetMs)
                    .Select(group => group.First())
                    .OrderBy(chapter => chapter.StartOffsetMs)
                    .ToList();

                if (chapters.Count < 2 || sourceDuration <= TimeSpan.Zero)
                {
                    return;
                }

                var referenceDuration = GetMatchedEditionChapterReferenceDuration(edition, chapters);
                if (referenceDuration <= TimeSpan.Zero)
                {
                    return;
                }

                var allowedDifference = AudiobookDurationTolerance.ForMatchingSeconds((int)Math.Round(referenceDuration.TotalSeconds, MidpointRounding.AwayFromZero));
                var actualDifference = (sourceDuration - referenceDuration).Duration();
                if (actualDifference > TimeSpan.FromSeconds(allowedDifference))
                {
                    _logger.Debug(
                        "[CONVERSION] Skipping provider chapters for '{0}' because source duration {1} does not match provider chapter duration {2}; allowed difference is {3}.",
                        book?.Title ?? edition.Title,
                        FormatDuration(sourceDuration),
                        FormatDuration(referenceDuration),
                        FormatDuration(TimeSpan.FromSeconds(allowedDifference)));
                    return;
                }

                var lastStart = TimeSpan.FromMilliseconds(chapters.Last().StartOffsetMs);
                if (lastStart >= sourceDuration)
                {
                    _logger.Debug(
                        "[CONVERSION] Skipping provider chapters for '{0}' because the last chapter starts after the source audio ends.",
                        book?.Title ?? edition.Title);
                    return;
                }

                tagOptions.ChaptersTxtContent = BuildChaptersTxt(chapters, sourceDuration);
                tagOptions.ProviderChapterCount = chapters.Count;
                _logger.Debug("[CONVERSION] Matched provider chapters will be stamped into converted M4B for '{0}' ({1} chapters).", book?.Title ?? edition.Title, chapters.Count);
            }

            private void ApplyConversionCoverFallback(ConversionTagOptions tagOptions, Book book, Edition matchedEdition)
            {
                if (tagOptions == null)
                {
                    return;
                }

                var fallbackPath = tagOptions.Cover.IsNotNullOrWhiteSpace()
                    ? tagOptions.Cover
                    : TryResolveMatchedBookCoverPath(book, matchedEdition);

                tagOptions.CoverPolicySignature = BuildConversionCoverPolicySignature(fallbackPath);

                if (fallbackPath.IsNullOrWhiteSpace() || tagOptions.Cover.IsNotNullOrWhiteSpace())
                {
                    return;
                }

                tagOptions.Cover = fallbackPath;
                _logger.Debug("[CONVERSION] Using matched book cover as fallback for '{0}': {1}", book?.Title ?? matchedEdition?.Title ?? "unknown book", fallbackPath);
            }

            private void TryApplySourceSidecarCover(ConversionTagOptions tagOptions, string[] inputFiles, Book book, Edition matchedEdition)
            {
                if (tagOptions == null || tagOptions.Cover.IsNotNullOrWhiteSpace())
                {
                    return;
                }

                var coverPath = TryFindSourceSidecarCover(inputFiles, book, matchedEdition);
                if (coverPath.IsNullOrWhiteSpace())
                {
                    return;
                }

                tagOptions.Cover = coverPath;
                tagOptions.CoverIsSource = true;
                _logger.Debug("[CONVERSION] Using source sidecar cover art for '{0}': {1}", book?.Title ?? matchedEdition?.Title ?? "unknown book", coverPath);
            }

            private static string TryFindSourceSidecarCover(IEnumerable<string> inputFiles, Book book, Edition matchedEdition)
            {
                var directories = inputFiles?
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Select(Path.GetDirectoryName)
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                var titleKeys = BuildSourceCoverTitleKeys(book, matchedEdition);

                foreach (var directory in directories)
                {
                    foreach (var baseName in SourceCoverBaseNames)
                    {
                        foreach (var extension in SourceCoverExtensions)
                        {
                            var candidate = Path.Combine(directory, baseName + extension);
                            if (IsUsableSourceCover(candidate))
                            {
                                return candidate;
                            }
                        }
                    }

                    var looseCandidate = TryFindLooseSourceCover(directory, titleKeys);
                    if (looseCandidate.IsNotNullOrWhiteSpace())
                    {
                        return looseCandidate;
                    }
                }

                return null;
            }

            private static string TryFindLooseSourceCover(string directory, IReadOnlyCollection<string> titleKeys)
            {
                try
                {
                    return Directory
                        .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(path => IsM4bToolCoverExtension(Path.GetExtension(path)))
                        .Where(IsUsableSourceCover)
                        .Select(path => new { Path = path, Rank = SourceCoverRank(path, titleKeys) })
                        .Where(candidate => candidate.Rank < int.MaxValue)
                        .OrderBy(candidate => candidate.Rank)
                        .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
                        .Select(candidate => candidate.Path)
                        .FirstOrDefault();
                }
                catch
                {
                    return null;
                }
            }

            private static IReadOnlyCollection<string> BuildSourceCoverTitleKeys(Book book, Edition matchedEdition)
            {
                return new[] { book?.Title, matchedEdition?.Title }
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .Select(NormalizeSourceCoverName)
                    .Where(value => value.Length >= 8)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private static string NormalizeSourceCoverName(string value)
            {
                if (value.IsNullOrWhiteSpace())
                {
                    return string.Empty;
                }

                var normalized = new StringBuilder(value.Length);
                foreach (var ch in value)
                {
                    if (char.IsLetterOrDigit(ch))
                    {
                        normalized.Append(char.ToLowerInvariant(ch));
                    }
                }

                return normalized.ToString();
            }

            private static int SourceCoverRank(string path, IReadOnlyCollection<string> titleKeys)
            {
                var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

                for (var i = 0; i < SourceCoverBaseNames.Length; i++)
                {
                    if (name.Equals(SourceCoverBaseNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                if (name.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SourceCoverBaseNames.Length;
                }

                if (name.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SourceCoverBaseNames.Length + 1;
                }

                var normalizedName = NormalizeSourceCoverName(name);
                if (normalizedName.Length >= 8 &&
                    titleKeys != null &&
                    titleKeys.Any(titleKey =>
                        titleKey.Contains(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                        normalizedName.Contains(titleKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return SourceCoverBaseNames.Length + 2;
                }

                return int.MaxValue;
            }

            private static bool IsUsableSourceCover(string path)
            {
                if (path.IsNullOrWhiteSpace() || !IsM4bToolCoverExtension(Path.GetExtension(path)))
                {
                    return false;
                }

                try
                {
                    var info = new FileInfo(path);
                    return info.Exists && info.Length > 0;
                }
                catch
                {
                    return false;
                }
            }

            private string TryResolveMatchedBookCoverPath(Book book, Edition matchedEdition)
            {
                if (_coverMapper == null || book == null || book.Id <= 0)
                {
                    return null;
                }

                EnsureCoverEditionsLoaded(book, matchedEdition);

                foreach (var cover in EnumerateConversionCoverCandidates(book, matchedEdition))
                {
                    var coverPath = GetExistingBookCoverPath(book.Id, cover.Extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                var existingCoverPath = TryFindAnyExistingBookCoverPath(book.Id);
                if (existingCoverPath.IsNotNullOrWhiteSpace())
                {
                    return existingCoverPath;
                }

                try
                {
                    _coverMapper.EnsureBookCovers(book);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[CONVERSION] Unable to ensure matched book cover before M4B conversion for '{0}'", book.Title);
                }

                foreach (var cover in EnumerateConversionCoverCandidates(book, matchedEdition))
                {
                    var coverPath = GetExistingBookCoverPath(book.Id, cover.Extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                // Last-resort sweep for sparse rows where image metadata is missing but a cached cover exists.
                return TryFindAnyExistingBookCoverPath(book.Id);
            }

            private void EnsureCoverEditionsLoaded(Book book, Edition matchedEdition)
            {
                var editions = book.Editions?.Where(e => e != null).ToList() ?? new List<Edition>();

                if (matchedEdition != null && !editions.Any(e => e.Id == matchedEdition.Id))
                {
                    editions.Insert(0, matchedEdition);
                }

                if (editions.Count == 0)
                {
                    try
                    {
                        editions = _editionService.GetEditionsByBook(book.Id)?.Where(e => e != null).ToList() ?? new List<Edition>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to load editions for matched book cover lookup: {0}", book.Title);
                    }

                    if (matchedEdition != null && !editions.Any(e => e.Id == matchedEdition.Id))
                    {
                        editions.Insert(0, matchedEdition);
                    }
                }

                book.Editions = editions;
            }

            private IEnumerable<NzbDrone.Core.MediaCover.MediaCover> EnumerateConversionCoverCandidates(Book book, Edition matchedEdition)
            {
                foreach (var cover in GetUsableCoverImages(matchedEdition?.Images))
                {
                    yield return cover;
                }

                foreach (var cover in GetUsableCoverImages(book?.Images))
                {
                    yield return cover;
                }

                foreach (var edition in book?.Editions ?? new List<Edition>())
                {
                    if (matchedEdition != null && edition.Id == matchedEdition.Id)
                    {
                        continue;
                    }

                    foreach (var cover in GetUsableCoverImages(edition.Images))
                    {
                        yield return cover;
                    }
                }
            }

            private static IEnumerable<NzbDrone.Core.MediaCover.MediaCover> GetUsableCoverImages(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers)
            {
                return covers?
                    .Where(c => c != null &&
                                c.CoverType == MediaCoverTypes.Cover &&
                                c.Url.IsNotNullOrWhiteSpace() &&
                                IsM4bToolCoverExtension(c.Extension))
                    .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()) ?? Enumerable.Empty<NzbDrone.Core.MediaCover.MediaCover>();
            }

            private string TryFindAnyExistingBookCoverPath(int bookId)
            {
                foreach (var extension in new[] { ".jpg", ".jpeg", ".png" })
                {
                    var coverPath = GetExistingBookCoverPath(bookId, extension);
                    if (coverPath.IsNotNullOrWhiteSpace())
                    {
                        return coverPath;
                    }
                }

                return null;
            }

            private static bool IsM4bToolCoverExtension(string extension)
            {
                return extension.IsNotNullOrWhiteSpace() &&
                       (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
            }

            private string GetExistingBookCoverPath(int bookId, string extension)
            {
                if (extension.IsNullOrWhiteSpace())
                {
                    return null;
                }

                try
                {
                    var coverPath = _coverMapper.GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, extension);
                    if (coverPath.IsNotNullOrWhiteSpace() && File.Exists(coverPath) && new FileInfo(coverPath).Length > 0)
                    {
                        return coverPath;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[CONVERSION] Unable to resolve local book cover path for book {0}", bookId);
                }

                return null;
            }

            private static string BuildConversionCoverPolicySignature(string fallbackPath)
            {
                var signature = new StringBuilder("source-cover-v1");

                if (fallbackPath.IsNullOrWhiteSpace())
                {
                    return signature.Append("|db:none").ToString();
                }

                signature.Append("|db:").Append(fallbackPath);

                try
                {
                    var info = new FileInfo(fallbackPath);
                    if (info.Exists)
                    {
                        signature
                            .Append('|')
                            .Append(info.Length.ToString(CultureInfo.InvariantCulture))
                            .Append('|')
                            .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch
                {
                    // Best-effort signature only; the path itself still invalidates older coverless artifacts.
                }

                return signature.ToString();
            }

            private static TimeSpan GetMatchedEditionChapterReferenceDuration(Edition edition, IReadOnlyList<EditionChapter> chapters)
            {
                var chapterEndMs = chapters
                    .Where(chapter => chapter.LengthMs > 0)
                    .Select(chapter => chapter.StartOffsetMs + chapter.LengthMs)
                    .DefaultIfEmpty(0)
                    .Max();

                if (chapterEndMs > 0)
                {
                    return TimeSpan.FromMilliseconds(chapterEndMs);
                }

                if (MediaDuration.HasDuration(edition?.DurationSeconds))
                {
                    return TimeSpan.FromSeconds(edition.DurationSeconds.Value);
                }

                return TimeSpan.Zero;
            }

            private static string BuildChaptersTxt(IReadOnlyList<EditionChapter> chapters, TimeSpan sourceDuration)
            {
                var lines = new List<string>
                {
                    "## total-length " + FormatChapterTime(sourceDuration)
                };

                lines.AddRange(chapters.Select(chapter =>
                    FormatChapterTime(TimeSpan.FromMilliseconds(chapter.StartOffsetMs)) + " " + SanitizeChapterTitle(chapter.Title)));

                return string.Join(Environment.NewLine, lines);
            }

            private static string FormatChapterTime(TimeSpan value)
            {
                var hours = (int)Math.Floor(value.TotalHours);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
                    hours,
                    value.Minutes,
                    value.Seconds,
                    value.Milliseconds);
            }

            private static string FormatDuration(TimeSpan value)
            {
                return FormatChapterTime(value);
            }

            private static string SanitizeChapterTitle(string title)
            {
                return string.Join(" ", (title ?? string.Empty)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Replace('\t', ' ')
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            private string[] TryCreateProviderChapterSidecarInputs(string workFolder, string[] inputFiles, ConversionTagOptions tagOptions, Book book)
            {
                if (tagOptions?.ChaptersTxtContent.IsNullOrWhiteSpace() != false)
                {
                    return inputFiles;
                }

                try
                {
                    var stageFolder = Path.Combine(workFolder, "provider-chapters");
                    Directory.CreateDirectory(stageFolder);
                    File.WriteAllText(Path.Combine(stageFolder, "chapters.txt"), tagOptions.ChaptersTxtContent, Encoding.UTF8);

                    var stagedInputs = new string[inputFiles.Length];
                    for (var index = 0; index < inputFiles.Length; index++)
                    {
                        var extension = Path.GetExtension(inputFiles[index]);
                        if (extension.IsNullOrWhiteSpace())
                        {
                            extension = ".audio";
                        }

                        var stagedPath = Path.Combine(stageFolder, (index + 1).ToString("D4", CultureInfo.InvariantCulture) + extension);
                        File.CreateSymbolicLink(stagedPath, inputFiles[index]);
                        stagedInputs[index] = stagedPath;
                    }

                    return stagedInputs;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to stage provider chapters for '{0}'. Falling back to source/filename chapters.", book?.Title ?? "unknown book");
                    tagOptions.ChaptersTxtContent = null;
                    tagOptions.ProviderChapterCount = null;
                    return inputFiles;
                }
            }

            private void PublishConversionFailed(LocalBook source, IEnumerable<string> inputFiles, Book book, Author author, QualityModel targetQuality, string outputPath, string error, DownloadClientItem downloadClientItem)
            {
                _eventAggregator.PublishEvent(new BookFileConversionFailedEvent(source, inputFiles, book, author, targetQuality, outputPath, error, downloadClientItem));
            }

            private LocalBook CreateGeneratedConversionLocalBook(LocalBook first, IReadOnlyList<ImportDecision<LocalBook>> bookDecisions, string outputPath, string[] inputFiles, QualityModel convertedQuality, ConversionTagOptions tagOptions)
            {
                var outputInfo = new FileInfo(outputPath);
                return new LocalBook
                {
                    Path = outputPath,
                    Size = outputInfo.Length,
                    Modified = outputInfo.LastWriteTimeUtc,
                    RawTags = first.RawTags,
                    FolderTrackInfo = first.FolderTrackInfo,
                    DownloadClientBookInfo = first.DownloadClientBookInfo,
                    AcoustIdResults = first.AcoustIdResults,
                    Author = first.Author,
                    Book = first.Book,
                    Edition = first.Edition,
                    Quality = convertedQuality,
                    IndexerFlags = first.IndexerFlags,
                    ExistingFile = false,
                    IsGeneratedConversion = true,
                    GeneratedConversionSourcePaths = inputFiles.ToList(),
                    GeneratedConversionOutputPath = outputPath,
                    GeneratedConversionOutputSize = outputInfo.Length,
                    GeneratedConversionSourceQuality = first.Quality,
                    GeneratedConversionTagMode = tagOptions.Mode,
                    GeneratedConversionTagManifestJson = tagOptions.ManifestJson,
                    AdditionalFile = first.AdditionalFile,
                    SceneSource = first.SceneSource,
                    ReleaseGroup = first.ReleaseGroup,
                    IsGraphicAudio = first.IsGraphicAudio,
                    AudioProductionType = first.AudioProductionType,
                    SceneName = first.SceneName,
                    Narrator = first.Narrator,
                    IsInitialImport = first.IsInitialImport,
                    IsManualImport = first.IsManualImport,
                    MatchProvenance = first.MatchProvenance,
                    CommonTags = first.CommonTags
                };
            }

            private string GetConvertedImportDestinationPath(LocalBook source, Book book, Author author, string outputName, QualityModel convertedQuality)
            {
                if (source?.Edition == null)
                {
                    throw new InvalidOperationException("Unable to determine converted M4B destination because the matched edition is missing.");
                }

                source.Edition.Book ??= book;

                var sourceFolder = Path.GetDirectoryName(source.Path);
                var previewPath = sourceFolder.IsNotNullOrWhiteSpace()
                    ? Path.Combine(sourceFolder, outputName + ".m4b")
                    : Path.GetFullPath(outputName + ".m4b");

                var previewLocalBook = new LocalBook
                {
                    Path = previewPath,
                    Author = author,
                    Book = book,
                    Edition = source.Edition,
                    Quality = convertedQuality,
                    Part = source.Part > 0 ? source.Part : 1,
                    PartCount = source.PartCount
                };

                var previewBookFile = new BookFile
                {
                    Path = previewLocalBook.Path.CleanFilePath(),
                    Quality = convertedQuality,
                    EditionId = source.Edition.Id,
                    Edition = source.Edition,
                    Author = author,
                    Part = previewLocalBook.Part,
                    PartCount = previewLocalBook.PartCount,
                    MediaType = BookFile.DetermineMediaType(convertedQuality)
                };

                return _bookFileMover.GetImportDestinationPath(previewBookFile, previewLocalBook);
            }

            private string GetConversionDestinationConflictReason(string finalDestinationPath, Book book, QualityProfile qualityProfile, bool replaceExisting, bool downloadForced)
            {
                if (finalDestinationPath.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var existingTracked = _mediaFileService.GetFileWithPath(finalDestinationPath);
                if (!DestinationFileExists(finalDestinationPath))
                {
                    if (existingTracked != null)
                    {
                        _logger.Warn("[CONVERSION] Preserving tracked BookFile row for currently-missing conversion destination. A successful import will update it in place; a trustworthy root scan owns missing-file cleanup. BookFileId={0}, EditionId={1}, Path={2}",
                            existingTracked.Id,
                            existingTracked.EditionId,
                            finalDestinationPath);
                    }

                    return null;
                }

                if (CanReplaceExistingConversionDestination(existingTracked, book, qualityProfile, replaceExisting, downloadForced))
                {
                    return null;
                }

                if (existingTracked != null)
                {
                    if (existingTracked.EditionId <= 0)
                    {
                        return $"Conversion skipped because an unmapped file already exists at the destination: {finalDestinationPath}. It may not appear in this author's Files tab. Import or delete the unmapped file, or change naming settings, then retry.";
                    }

                    return $"Conversion skipped because the destination is already occupied: {finalDestinationPath}. Remove or rename the existing file, or change naming settings, then retry.";
                }

                return $"Conversion skipped because an untracked file already exists at the destination: {finalDestinationPath}. It will not appear in Chaptarr's Files tab. Remove or rename the file, or change naming settings, then retry.";
            }

            private bool CanReplaceExistingConversionDestination(BookFile existingTracked, Book book, QualityProfile qualityProfile, bool replaceExisting, bool downloadForced)
            {
                if (!replaceExisting || (!downloadForced && qualityProfile?.UpgradeAllowed != true) || existingTracked == null)
                {
                    return false;
                }

                var existingEdition = existingTracked.Edition;
                if (existingEdition == null && existingTracked.EditionId > 0)
                {
                    existingEdition = _editionService.GetEdition(existingTracked.EditionId);
                }

                return existingEdition?.BookId == book.Id;
            }

            private bool TryFindReusableConversionArtifact(string workRoot, string[] inputFiles, QualityModel targetQuality, ConversionTagOptions tagOptions, int audioBitrate, int audioChannels, out string outputPath)
            {
                outputPath = null;
                if (workRoot.IsNullOrWhiteSpace() || !Directory.Exists(workRoot))
                {
                    return false;
                }

                var currentSources = BuildConversionArtifactSourceSignatures(inputFiles);
                if (currentSources == null || currentSources.Count == 0)
                {
                    return false;
                }

                var tagSignature = GetConversionTagSignature(tagOptions);
                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory
                        .EnumerateFiles(workRoot, ConversionArtifactManifestFileName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Failed checking retained conversion artifacts under {0}", workRoot);
                    return false;
                }

                foreach (var manifestPath in manifests)
                {
                    try
                    {
                        var manifest = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(manifestPath));
                        if (manifest == null ||
                            manifest.TargetQualityId != targetQuality?.Quality?.Id ||
                            manifest.AudioBitrate != audioBitrate ||
                            manifest.AudioChannels != audioChannels ||
                            !string.Equals(manifest.TagSignature, tagSignature, StringComparison.Ordinal) ||
                            !ConversionArtifactSourcesMatch(currentSources, manifest.Sources))
                        {
                            continue;
                        }

                        var candidateOutput = manifest.OutputPath;
                        if (candidateOutput.IsNullOrWhiteSpace())
                        {
                            candidateOutput = Directory
                                .EnumerateFiles(Path.GetDirectoryName(manifestPath), "*.m4b", SearchOption.TopDirectoryOnly)
                                .OrderByDescending(File.GetLastWriteTimeUtc)
                                .FirstOrDefault();
                        }

                        if (!candidateOutput.IsNullOrWhiteSpace() &&
                            IsConversionWorkFolder(candidateOutput) &&
                            File.Exists(candidateOutput))
                        {
                            outputPath = candidateOutput;
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[CONVERSION] Ignoring unreadable retained conversion artifact manifest: {0}", manifestPath);
                    }
                }

                return false;
            }

            private void WriteConversionArtifactManifest(string workFolder, string outputPath, string[] inputFiles, QualityModel targetQuality, ConversionTagOptions tagOptions, int audioBitrate, int audioChannels)
            {
                try
                {
                    var sources = BuildConversionArtifactSourceSignatures(inputFiles);
                    if (sources == null || sources.Count == 0)
                    {
                        return;
                    }

                    var manifest = new ConversionArtifactManifest
                    {
                        CreatedUtc = DateTime.UtcNow,
                        OutputPath = outputPath,
                        TargetQualityId = targetQuality?.Quality?.Id ?? 0,
                        TargetQualityName = targetQuality?.Quality?.Name,
                        AudioBitrate = audioBitrate,
                        AudioChannels = audioChannels,
                        TagMode = tagOptions?.Mode,
                        TagSignature = GetConversionTagSignature(tagOptions),
                        Sources = sources
                    };

                    Directory.CreateDirectory(workFolder);
                    File.WriteAllText(
                        Path.Combine(workFolder, ConversionArtifactManifestFileName),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Failed writing conversion artifact manifest for {0}", outputPath);
                }
            }

            private static List<ConversionArtifactSource> BuildConversionArtifactSourceSignatures(IEnumerable<string> inputFiles)
            {
                var sources = new List<ConversionArtifactSource>();
                foreach (var inputFile in inputFiles.Where(p => !p.IsNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(inputFile))
                    {
                        return null;
                    }

                    var info = new FileInfo(inputFile);
                    sources.Add(new ConversionArtifactSource
                    {
                        Path = inputFile,
                        Size = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
                    });
                }

                return sources;
            }

            private static bool ConversionArtifactSourcesMatch(IReadOnlyList<ConversionArtifactSource> currentSources, IReadOnlyList<ConversionArtifactSource> manifestSources)
            {
                if (currentSources == null || manifestSources == null || currentSources.Count != manifestSources.Count)
                {
                    return false;
                }

                return currentSources
                    .Zip(manifestSources, (current, candidate) =>
                        !candidate.Path.IsNullOrWhiteSpace() &&
                        candidate.Path.PathEquals(current.Path) &&
                        candidate.Size == current.Size &&
                        candidate.ModifiedUtcTicks == current.ModifiedUtcTicks)
                    .All(matches => matches);
            }

            private static string GetConversionTagSignature(ConversionTagOptions tagOptions)
            {
                if (tagOptions == null)
                {
                    return string.Empty;
                }

                return string.Join("\u001f",
                    tagOptions.Mode,
                    tagOptions.Name,
                    tagOptions.Album,
                    tagOptions.Artist,
                    tagOptions.AlbumArtist,
                    tagOptions.Writer,
                    tagOptions.Year,
                    tagOptions.Genre,
                    tagOptions.Comment,
                    tagOptions.Copyright,
                    tagOptions.Grouping,
                    tagOptions.Series,
                    tagOptions.SeriesPart,
                    tagOptions.Cover,
                    tagOptions.CoverIsSource.ToString(CultureInfo.InvariantCulture),
                    tagOptions.EncodedBy,
                    tagOptions.UseFilenamesAsChapters.ToString(CultureInfo.InvariantCulture),
                    tagOptions.IgnoreSourceTags.ToString(CultureInfo.InvariantCulture),
                    tagOptions.ProviderChapterCount?.ToString(CultureInfo.InvariantCulture),
                    tagOptions.CoverPolicySignature,
                    HashString(tagOptions.ChaptersTxtContent));
            }

            private static string HashString(string value)
            {
                if (value.IsNullOrWhiteSpace())
                {
                    return string.Empty;
                }

                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            }

            private ConversionEstimate EstimateConversionWorkspace(string[] inputFiles)
            {
                try
                {
                    return _m4bConversionService.EstimateConversion(inputFiles);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to estimate conversion workspace size; continuing without size estimate");
                    return null;
                }
            }

            private int GetAudiobookConversionBitrate(string[] inputFiles)
            {
                var maxBitrate = Clamp(_configService?.AudiobookConversionMaxBitrate ?? 64, 16, 320);
                if (_configService?.AudiobookConversionNoUpscale != true)
                {
                    return maxBitrate;
                }

                var sourceBitrate = GetLowestSourceAudioBitrate(inputFiles);
                if (!sourceBitrate.HasValue)
                {
                    return maxBitrate;
                }

                return Math.Min(maxBitrate, Clamp(sourceBitrate.Value, 16, 320));
            }

            private (int ParallelFiles, int FfmpegThreads) GetAudiobookConversionThreadPlan(int inputFileCount)
            {
                var tokenBudget = Clamp(_configService?.AudiobookConversionMaxCpuThreads ?? 4, 1, 64);
                var sourceFiles = Math.Max(1, inputFileCount);

                var parallelFiles = Math.Min(sourceFiles, tokenBudget);
                var ffmpegThreads = Math.Max(1, tokenBudget / parallelFiles);

                return (parallelFiles, ffmpegThreads);
            }

            private SemaphoreSlim GetAudiobookConversionSemaphore()
            {
                var concurrentConversions = GetAudiobookConversionConcurrentConversions();

                lock (ConversionSemaphoreLock)
                {
                    if (_conversionSemaphoreLimit != concurrentConversions &&
                        _conversionSemaphore.CurrentCount == _conversionSemaphoreLimit)
                    {
                        // Do not dispose the old semaphore: another importer may already hold a
                        // reference between lookup and Wait(). The old instance becomes collectible.
                        _conversionSemaphore = new SemaphoreSlim(concurrentConversions, concurrentConversions);
                        _conversionSemaphoreLimit = concurrentConversions;
                    }

                    return _conversionSemaphore;
                }
            }

            private int GetAudiobookConversionConcurrentConversions()
            {
                return Clamp(_configService?.AudiobookConversionConcurrentConversions ?? 1, 1, 16);
            }

            private int GetAudiobookConversionAudioChannels()
            {
                return string.Equals(_configService?.AudiobookConversionAudioChannels, "mono", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }

            private int? GetLowestSourceAudioBitrate(string[] inputFiles)
            {
                var bitrates = new List<int>();

                foreach (var inputFile in inputFiles ?? Array.Empty<string>())
                {
                    try
                    {
                        var mediaInfo = _mediaInfoExtractor.ExtractMediaInfo(inputFile);
                        if (mediaInfo?.AudioBitrate > 0)
                        {
                            bitrates.Add(mediaInfo.AudioBitrate);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to read source bitrate for {0}", inputFile);
                    }
                }

                return bitrates.Count > 0 ? bitrates.Min() : null;
            }

            private TimeSpan GetAudiobookConversionSourceDuration(string[] inputFiles)
            {
                var total = TimeSpan.Zero;

                foreach (var inputFile in inputFiles ?? Array.Empty<string>())
                {
                    try
                    {
                        var duration = _mediaInfoExtractor.GetDuration(inputFile);
                        if (duration <= TimeSpan.Zero)
                        {
                            return TimeSpan.Zero;
                        }

                        total += duration;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[CONVERSION] Unable to read source duration for {0}", inputFile);
                        return TimeSpan.Zero;
                    }
                }

                return total;
            }

            private static int Clamp(int value, int min, int max)
            {
                return Math.Min(max, Math.Max(min, value));
            }

            private bool HasEnoughConversionWorkspaceSpace(string destinationFolder, ConversionEstimate estimate, out string error)
            {
                error = null;

                if (_configService?.SkipFreeSpaceCheckWhenImporting == true)
                {
                    _logger.Debug("[CONVERSION] Skipping destination free space check because import free space checks are disabled");
                    return true;
                }

                if (_diskProvider == null || _configService == null)
                {
                    _logger.Debug("[CONVERSION] Skipping destination free space check because disk/config services are unavailable");
                    return true;
                }

                try
                {
                    var checkPath = GetExistingFolderForFreeSpaceCheck(destinationFolder);
                    var freeSpace = _diskProvider.GetAvailableSpace(checkPath);
                    if (!freeSpace.HasValue)
                    {
                        _logger.Debug("[CONVERSION] Free space check returned no result for conversion destination: {0}", checkPath);
                        return true;
                    }

                    var estimatedInputSize = Math.Max(0, estimate?.TotalInputSize ?? 0);
                    var estimatedOutputSize = Math.Max(estimate?.EstimatedOutputSize ?? 0, estimatedInputSize);
                    var minimumFreeSpace = Math.Max(0, _configService.MinimumFreeSpaceWhenImporting).Megabytes();
                    var requiredFreeSpace = estimatedInputSize + estimatedOutputSize + minimumFreeSpace;
                    if (freeSpace.Value >= requiredFreeSpace)
                    {
                        return true;
                    }

                    error = $"Not enough free space in conversion destination folder. Need about {FormatBytes(requiredFreeSpace)} available at {checkPath}; {FormatBytes(freeSpace.Value)} is available.";
                    _logger.Warn("[CONVERSION] {0}", error);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to check free disk space for conversion destination: {0}", destinationFolder);
                    return true;
                }
            }

            private static string GetExistingFolderForFreeSpaceCheck(string folder)
            {
                var current = folder;
                while (current.IsNotNullOrWhiteSpace() && !Directory.Exists(current))
                {
                    var parent = Directory.GetParent(current);
                    if (parent == null)
                    {
                        return current;
                    }

                    current = parent.FullName;
                }

                return current;
            }

            private static string FormatBytes(long bytes)
            {
                var value = (double)Math.Max(0, bytes);
                var suffixIndex = 0;
                var suffixes = new[] { "B", "KB", "MB", "GB", "TB" };

                while (value >= 1024 && suffixIndex < suffixes.Length - 1)
                {
                    value /= 1024;
                    suffixIndex++;
                }

                return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, suffixes[suffixIndex]);
            }

            private static string GetUniqueFileName(string baseName, string extension, HashSet<string> usedNames)
            {
                var candidate = baseName + extension;
                var index = 2;

                while (!usedNames.Add(candidate))
                {
                    candidate = $"{baseName}-{index++}{extension}";
                }

                return candidate;
            }

            private static string GetSafeConvertedFileName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return "converted";
                }

                var invalid = Path.GetInvalidFileNameChars();
                var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
                return string.IsNullOrWhiteSpace(safe) ? "converted" : safe;
            }

            private void CleanupConversionWorkFolder(string workFolder)
            {
                if (string.IsNullOrWhiteSpace(workFolder))
                {
                    return;
                }

                try
                {
                    var fullPath = Path.GetFullPath(workFolder);
                    if (!IsConversionWorkFolder(fullPath))
                    {
                        _logger.Warn("[CONVERSION] Refusing to clean non-conversion work folder: {0}", fullPath);
                        return;
                    }

                    if (Directory.Exists(fullPath))
                    {
                        Directory.Delete(fullPath, recursive: true);
                        DeleteEmptyConversionParent(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Failed to clean conversion work folder: {0}", workFolder);
                }
            }

            private void CleanupExpiredConversionArtifacts(string conversionParentFolder, TimeSpan maxAge)
            {
                if (string.IsNullOrWhiteSpace(conversionParentFolder))
                {
                    return;
                }

                try
                {
                    var fullPath = Path.GetFullPath(conversionParentFolder);
                    if (!IsConversionWorkFolder(fullPath) || !Directory.Exists(fullPath))
                    {
                        return;
                    }

                    var cutoff = DateTime.UtcNow.Subtract(maxAge);
                    foreach (var downloadFolder in Directory.EnumerateDirectories(fullPath))
                    {
                        foreach (var attemptFolder in Directory.EnumerateDirectories(downloadFolder))
                        {
                            var attemptInfo = new DirectoryInfo(attemptFolder);
                            if (attemptInfo.LastWriteTimeUtc >= cutoff)
                            {
                                continue;
                            }

                            _logger.Debug("[CONVERSION] Removing retained conversion artifact older than {0:g}: {1}", maxAge, attemptFolder);
                            Directory.Delete(attemptFolder, recursive: true);
                        }

                        if (!Directory.EnumerateFileSystemEntries(downloadFolder).Any())
                        {
                            Directory.Delete(downloadFolder, recursive: false);
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
                    {
                        Directory.Delete(fullPath, recursive: false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Failed to clean expired retained conversion artifacts under: {0}", conversionParentFolder);
                }
            }

            private void CleanupOrRetainConversionWorkFolder(string workFolder, List<ImportDecision<LocalBook>> bookDecisions, string reason)
            {
                if (TryGetRetainableGeneratedConversionPath(bookDecisions, out var retainedPath))
                {
                    _logger.Warn("[CONVERSION] Retaining converted M4B after import failure. Path={0} Reason={1}", retainedPath, reason);
                    return;
                }

                CleanupConversionWorkFolder(workFolder);
            }

            private string AppendRetainedConversionPath(string message, LocalBook convertedLocalBook)
            {
                if (TryGetRetainableGeneratedConversionPath(convertedLocalBook, out var retainedPath))
                {
                    var prefix = message.IsNullOrWhiteSpace() ? "Converted M4B was not imported." : message;
                    return $"{prefix} Converted file retained at: {retainedPath}";
                }

                return message;
            }

            private static bool TryGetRetainableGeneratedConversionPath(IEnumerable<ImportDecision<LocalBook>> bookDecisions, out string retainedPath)
            {
                retainedPath = null;
                var convertedLocalBook = bookDecisions?
                    .Select(d => d?.Item)
                    .FirstOrDefault(i => i?.IsGeneratedConversion == true);

                return TryGetRetainableGeneratedConversionPath(convertedLocalBook, out retainedPath);
            }

            private static bool TryGetRetainableGeneratedConversionPath(LocalBook convertedLocalBook, out string retainedPath)
            {
                retainedPath = convertedLocalBook?.GeneratedConversionOutputPath ?? convertedLocalBook?.Path;
                return !retainedPath.IsNullOrWhiteSpace() &&
                       IsConversionWorkFolder(retainedPath) &&
                       File.Exists(retainedPath);
            }

            private bool DestinationFileExists(string path)
            {
                if (path.IsNullOrWhiteSpace())
                {
                    return false;
                }

                try
                {
                    return _diskProvider?.FileExists(path) ?? File.Exists(path);
                }
                catch
                {
                    return File.Exists(path);
                }
            }

            private static bool IsConversionWorkFolder(string path)
            {
                return path
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("chaptarr-conversions", StringComparison.OrdinalIgnoreCase));
            }

            private static void DeleteEmptyConversionParent(string path)
            {
                var parent = Directory.GetParent(path);
                if (parent == null ||
                    !parent.Name.Equals(".chaptarr-conversions", StringComparison.OrdinalIgnoreCase) ||
                    Directory.EnumerateFileSystemEntries(parent.FullName).Any())
                {
                    return;
                }

                Directory.Delete(parent.FullName, recursive: false);
            }

            private static bool IsBookFilesPathUniqueViolation(SqliteException ex)
            {
                const int sqliteConstraintUnique = 2067; // SQLITE_CONSTRAINT_UNIQUE
                if (ex.SqliteExtendedErrorCode != sqliteConstraintUnique)
                {
                    return false;
                }

                // Example: "UNIQUE constraint failed: BookFiles.Path"
                return ex.Message.Contains("BookFiles.Path", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsBookFilesPathUniqueViolation(PostgresException ex)
            {
                if (!string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
                {
                    return false;
                }

                var blob = $"{ex.ConstraintName} {ex.Detail}";
                return blob.Contains("IX_BookFiles_Path_Unique", StringComparison.OrdinalIgnoreCase) ||
                       blob.Contains("BookFiles", StringComparison.OrdinalIgnoreCase) && blob.Contains("Path", StringComparison.OrdinalIgnoreCase);
            }

            private void CommitPreparedBookFiles(List<BookFile> bookFilesToAdd, List<PendingFileCommit> pendingFileCommits)
            {
                var rowsToReplace = GetDatabaseRowsToReplace(pendingFileCommits);
                _mediaFileService.ReplaceMany(bookFilesToAdd, rowsToReplace, DeleteMediaFileReason.Upgrade);
            }

            private void RetryPreparedBookFilesAfterPathConflict(
                List<BookFile> bookFilesToAdd,
                List<PendingFileCommit> pendingFileCommits,
                Exception ex)
            {
                if (pendingFileCommits?.Any(commit => commit?.CalibreImport != null) == true)
                {
                    try
                    {
                        RollbackPendingFileCommits(pendingFileCommits);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("Calibre import persistence conflicted and compensation also failed.", ex, rollbackException);
                    }

                    throw new InvalidOperationException("Calibre import persistence conflicted with an existing destination path; the Calibre import was rolled back.", ex);
                }

                _logger.Warn(ex, "[IMPORT-PATH-CONFLICT] UNIQUE constraint hit during the atomic BookFile swap; refreshing destination rows and retrying once");

                try
                {
                    var rowsToReplace = GetDatabaseRowsToReplace(pendingFileCommits);
                    var knownIds = rowsToReplace.Where(file => file?.Id > 0).Select(file => file.Id).ToHashSet();

                    foreach (var bookFile in bookFilesToAdd.Where(file => file?.Path.IsNotNullOrWhiteSpace() == true))
                    {
                        var existing = _mediaFileService.GetFileWithPath(bookFile.Path);
                        if (existing?.Id > 0 && knownIds.Add(existing.Id))
                        {
                            _logger.Warn("[IMPORT-PATH-CONFLICT] Including concurrently-created BookFileId={0} in the atomic replacement. Path={1}",
                                existing.Id,
                                bookFile.Path);
                            rowsToReplace.Add(existing);
                        }
                    }

                    _mediaFileService.ReplaceMany(bookFilesToAdd, rowsToReplace, DeleteMediaFileReason.Upgrade);
                }
                catch (Exception retryException)
                {
                    try
                    {
                        RollbackPendingFileCommits(pendingFileCommits);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException("BookFile persistence retry failed and compensation also failed.", retryException, rollbackException);
                    }

                    throw;
                }
            }

            private static List<BookFile> GetDatabaseRowsToReplace(IEnumerable<PendingFileCommit> pendingFileCommits)
            {
                return pendingFileCommits?
                    .SelectMany(commit => commit?.DatabaseRowsToReplace ?? new List<BookFile>())
                    .Where(file => file?.Id > 0)
                    .DistinctBy(file => file.Id)
                    .ToList() ?? new List<BookFile>();
            }

            private BookFile ResolveBookFilePathConflict(BookFile bookFile, LocalBook localBook, PendingFileCommit pendingFileCommit)
            {
                if (bookFile == null || bookFile.Path.IsNullOrWhiteSpace())
                {
                    return bookFile;
                }

                // Destination path uniqueness is enforced at the DB level, but stale rows can remain after aborted imports,
                // root folder changes, or manual user moves.
                var existingAtDestination = _mediaFileService.GetFileWithPath(bookFile.Path);
                if (existingAtDestination == null || existingAtDestination.Id == bookFile.Id)
                {
                    return bookFile;
                }

                // If we're relocating an existing tracked file (bookFile.Id > 0), delete the stale conflicting row.
                if (bookFile.Id > 0)
                {
                    _logger.Warn("[IMPORT-PATH-CONFLICT] Destination path already tracked by BookFileId={0} (EditionId={1}). Deleting stale row to allow relocation of BookFileId={2}. Path={3}",
                        existingAtDestination.Id, existingAtDestination.EditionId, bookFile.Id, bookFile.Path);

                    try
                    {
                        _mediaFileService.Delete(existingAtDestination, DeleteMediaFileReason.ManualOverride);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "[IMPORT-PATH-CONFLICT] Failed deleting stale BookFileId={0} at path {1}", existingAtDestination.Id, bookFile.Path);
                    }

                    return bookFile;
                }

                if (bookFile.CalibreId == 0 && existingAtDestination.CalibreId != 0)
                {
                    bookFile.CalibreId = existingAtDestination.CalibreId;
                }

                if (pendingFileCommit == null)
                {
                    throw new InvalidOperationException("A new destination-path replacement must have a pending file commit");
                }

                if (pendingFileCommit.StagedReplacements.Any())
                {
                    _logger.Warn("[IMPORT-PATH-CONFLICT] Upgrade also found destination BookFileId={0}. Including every displaced row in the atomic replacement with EditionId={1}. Source={2} Dest={3}",
                        existingAtDestination.Id,
                        bookFile.EditionId,
                        localBook?.Path,
                        bookFile.Path);
                    pendingFileCommit.DatabaseRowsToReplace.Add(existingAtDestination);
                    return bookFile;
                }

                // A stale destination row was not displaced as part of this upgrade. Preserve the
                // established relink behavior, but keep the pending transfer available until the
                // update succeeds so a failed update can still reverse the disk move/copy.
                _logger.Warn("[IMPORT-PATH-CONFLICT] Destination path already tracked by BookFileId={0} (EditionId={1}). Updating that row to EditionId={2}. Source={3} Dest={4}",
                    existingAtDestination.Id,
                    existingAtDestination.EditionId,
                    bookFile.EditionId,
                    localBook?.Path,
                    bookFile.Path);

                bookFile.Id = existingAtDestination.Id;
                if ((bookFile.ReplicaPaths == null || bookFile.ReplicaPaths.Count == 0) && existingAtDestination.ReplicaPaths?.Count > 0)
                {
                    bookFile.ReplicaPaths = existingAtDestination.ReplicaPaths;
                }

                bookFile.DurationSeconds ??= existingAtDestination.DurationSeconds;
                _mediaFileService.Update(bookFile);
                pendingFileCommit.DatabaseCommitted = true;

                return bookFile;
            }

        private static string GetUniqueUpgradeBackupPath(string originalPath)
        {
            var directory = Path.GetDirectoryName(originalPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(originalPath)) ?? ".";
            }

            var fileName = Path.GetFileName(originalPath);

            // Same directory ensures same volume so rename is atomic and fast.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = Path.Combine(directory, $"{fileName}.chaptarr-upgrade~{Guid.NewGuid():N}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{fileName}.chaptarr-upgrade~{Guid.NewGuid():N}");
        }

        private void FinalizePendingFileCommits(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            var stopwatch = Stopwatch.StartNew();
            var finalized = 0;

            foreach (var commit in pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>())
            {
                if (commit.CalibreImport != null)
                {
                    try
                    {
                        _upgradeMediaFiles.CompleteCalibreImport(commit.CalibreImport);
                    }
                    catch (Exception ex)
                    {
                        // The database and Calibre now agree. Retaining the downloader source is
                        // safer than turning cleanup trouble into a failed import.
                        _logger.Error(ex, "[CLEAN-IMPORT] Calibre import committed, but source cleanup failed: {0}", commit.SourcePath);
                    }
                }

                foreach (var (oldFile, backupPath) in commit.StagedReplacements)
                {
                    try
                    {
                        if (!File.Exists(backupPath))
                        {
                            continue;
                        }

                        var subfolder = string.Empty;
                        try
                        {
                            var rootFolderPath = commit.Author?.GetRootFolderForQuality(commit.LocalBook.Quality.Quality);
                            var oldFileDirectory = Path.GetDirectoryName(backupPath);

                            if (!string.IsNullOrWhiteSpace(rootFolderPath) && !string.IsNullOrWhiteSpace(oldFileDirectory))
                            {
                                subfolder = rootFolderPath.GetRelativePath(oldFileDirectory);
                            }
                        }
                        catch
                        {
                            subfolder = string.Empty;
                        }

                        _recycleBinProvider.DeleteFile(backupPath, subfolder);
                        finalized++;
                    }
                    catch (Exception ex)
                    {
                        // The database now points at the new file. Retaining a clearly-named
                        // backup is safer than turning cleanup trouble into a false import failure.
                        _logger.Error(ex, "[CLEAN-IMPORT] New BookFile committed, but old backup could not be recycled and was retained: {0}", backupPath);
                    }
                }
            }

            stopwatch.Stop();
            if (finalized > 0)
            {
                _logger.Debug("[PERFORMANCE] Finalized {0} committed upgrade backups in {1}ms", finalized, stopwatch.ElapsedMilliseconds);
            }
        }

        private void WriteTagsForCommittedFiles(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            foreach (var commit in pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>())
            {
                if (commit.CalibreImport == null)
                {
                    TryWriteTags(commit.BookFile, true, "TRANSFER");
                }
            }
        }

        private void RollbackPendingFileCommits(IEnumerable<PendingFileCommit> pendingFileCommits)
        {
            var failures = new List<Exception>();
            foreach (var commit in (pendingFileCommits ?? Enumerable.Empty<PendingFileCommit>()).Reverse())
            {
                try
                {
                    RollbackPendingFileCommit(commit);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Any())
            {
                throw new AggregateException("One or more pending file compensations failed.", failures);
            }
        }

        private void RollbackPendingFileCommit(PendingFileCommit pendingFileCommit)
        {
            if (pendingFileCommit == null)
            {
                return;
            }

            if (pendingFileCommit.CalibreImport != null)
            {
                try
                {
                    _upgradeMediaFiles.RollbackCalibreImport(pendingFileCommit.CalibreImport);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to compensate Calibre import for '{pendingFileCommit.SourcePath}'.", ex);
                }

                return;
            }

            var destinationCleared = RollbackTransferredFile(pendingFileCommit);
            RollbackStagedReplacements(
                pendingFileCommit.StagedReplacements,
                destinationCleared ? null : pendingFileCommit.DestinationPath);

            if (!destinationCleared && _diskProvider != null)
            {
                // The failed transfer still exists at its destination and could not be
                // returned without overwriting a newly occupied source path. Keep that
                // real file visible to the user instead of leaving it row-less.
                BookImportUnmappedFileHelper.TryEnsureUnmapped(
                    _mediaFileService,
                    _diskProvider,
                    pendingFileCommit.DestinationPath,
                    _logger,
                    "[CLEAN-IMPORT-ROLLBACK]",
                    pendingFileCommit.BookFile?.AllTags,
                    pendingFileCommit.BookFile?.DurationSeconds);
            }
        }

        private bool RollbackTransferredFile(PendingFileCommit pendingFileCommit)
        {
            var sourcePath = pendingFileCommit.SourcePath;
            var destinationPath = pendingFileCommit.DestinationPath;
            if (sourcePath.IsNullOrWhiteSpace() ||
                destinationPath.IsNullOrWhiteSpace() ||
                sourcePath.PathEquals(destinationPath) ||
                !File.Exists(destinationPath))
            {
                return true;
            }

            try
            {
                if (pendingFileCommit.CopyOnly)
                {
                    File.Delete(destinationPath);
                }
                else
                {
                    var sourceDirectory = Path.GetDirectoryName(sourcePath);
                    if (sourceDirectory.IsNotNullOrWhiteSpace())
                    {
                        Directory.CreateDirectory(sourceDirectory);
                    }

                    if (File.Exists(sourcePath))
                    {
                        _logger.Error("[CLEAN-IMPORT] Cannot move failed import back because the original source path is occupied. Retaining destination for recovery: {0}", destinationPath);
                        return false;
                    }

                    File.Move(destinationPath, sourcePath);
                }

                return !File.Exists(destinationPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[CLEAN-IMPORT] Failed to reverse transferred file after database failure: {0} -> {1}", destinationPath, sourcePath);
                return false;
            }
        }

        private void RollbackStagedReplacements(
            List<(BookFile OldFile, string BackupPath)> stagedReplacements,
            string blockedDestinationPath = null)
        {
            if (stagedReplacements == null || stagedReplacements.Count == 0)
            {
                return;
            }

            for (var i = stagedReplacements.Count - 1; i >= 0; i--)
            {
                var (oldFile, backupPath) = stagedReplacements[i];
                try
                {
                    if (oldFile == null || string.IsNullOrWhiteSpace(oldFile.Path) || string.IsNullOrWhiteSpace(backupPath))
                    {
                        continue;
                    }

                    if (!File.Exists(backupPath))
                    {
                        continue;
                    }

                    if (blockedDestinationPath.IsNotNullOrWhiteSpace() && oldFile.Path.PathEquals(blockedDestinationPath))
                    {
                        _logger.Error("[CLEAN-IMPORT] Retaining staged backup because the failed destination could not be cleared: {0}", backupPath);
                        continue;
                    }

                    // Overwrite any partially transferred file at the original path.
                    File.Move(backupPath, oldFile.Path, true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CLEAN-IMPORT] Failed to rollback staged replacement: {0} -> {1}", backupPath, oldFile?.Path);
                }
            }
        }

        private void TryWriteTags(BookFile bookFile, bool newDownload, string route)
        {
            try
            {
                _metadataTagService.WriteTags(bookFile, newDownload);
            }
            catch (Exception ex)
            {
                // Tags are optional post-processing. They cannot be part of the disk/database
                // commit and must never turn an otherwise valid import into invisible inventory.
                _logger.Warn(ex, "[{0}] Failed writing tags; keeping the BookFile import valid: {1}", route, bookFile?.Path);
            }
        }

        // Transfer logic is handled by IMoveBookFiles (BookFileMovingService)

        private bool ShouldMoveFile(LocalBook localBook, Author author)
        {
            // Only used for new downloads. If file already resides under the configured root
            // folder for its media type, don't move it.
            var fileDirectory = System.IO.Path.GetDirectoryName(localBook.Path);
            var rootFolder = author?.GetRootFolderForQuality(localBook.Quality.Quality);

            if (!string.IsNullOrWhiteSpace(rootFolder))
            {
                if (rootFolder.PathEquals(fileDirectory) || rootFolder.IsParentPath(localBook.Path))
                {
                    _logger.Debug("File already under root folder, not moving: {0}", localBook.Path);
                    return false;
                }
            }

            return true;
        }

        private bool DetectGraphicAudioFromLocalTags(LocalBook localBook)
        {
            if (localBook == null)
            {
                return false;
            }

            var mediaType = QualityMediaTypeHelper.GetKnownMediaType(localBook.Quality?.Quality ?? Quality.Unknown) ??
                            QualityMediaTypeHelper.GetMediaTypeFromPath(localBook.Path);

            if (mediaType != BookMediaType.Audiobook)
            {
                return false;
            }

            if (localBook.RawTags?.AllTags == null)
            {
                return false;
            }

            return AudioProductionDetector.IsDramatizedOrFullCast(localBook.RawTags.AllTags);
        }

        private int? GetDurationSeconds(LocalBook localBook)
        {
            if (MediaDuration.HasDuration(localBook?.DurationSeconds))
            {
                return localBook.DurationSeconds;
            }

            if (string.IsNullOrWhiteSpace(localBook?.Path))
            {
                return null;
            }

            return MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(localBook.Path));
        }

        private static string BuildImportUnitKeyHash(LocalBook localBook, Edition edition, BookMediaType mediaType)
        {
            if (localBook == null || string.IsNullOrWhiteSpace(localBook.Path))
            {
                return null;
            }

            try
            {
                var baseKey = BookCoalescingHelper.BuildRootUnitKey(localBook.Path, edition?.Title, mediaType);
                var extension = Path.GetExtension(localBook.Path) ?? string.Empty;
                var unitKey = BookCoalescingHelper.IsStandaloneUnitExtension(extension)
                    ? baseKey
                    : $"{baseKey}|{extension}";

                if (string.IsNullOrWhiteSpace(unitKey))
                {
                    return null;
                }

                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(unitKey.Trim()));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        private class BatchBookResult
        {
            public Book NewBook { get; set; }
            public Edition NewEdition { get; set; }
        }

        private BatchBookResult CreateNewBookInstanceForBatch(ImportDecision<LocalBook> decision, Book originalBook, Author author, bool anyEditionOk)
        {
            var localBook = decision.Item;
            var originalEdition = localBook.Edition;

            try
            {

                // Determine narrator identifier for unique slug (prefer edition narrators if available)
                string narratorIdentifier = null;
                if (originalEdition?.NarratorNames != null && originalEdition.NarratorNames.Any())
                {
                    narratorIdentifier = string.Join("_", originalEdition.NarratorNames).CleanAuthorName().ToLowerInvariant().Replace(" ", "_");
                }

                var mediaType = localBook.Quality.Quality.Id <= 4 ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var copySuffix = DateTime.UtcNow.Ticks.ToString().Substring(10);
                var unitKeyHash = BuildImportUnitKeyHash(localBook, originalEdition, mediaType);

                // Generate unique copy slug. Numeric suffixes look like canonical sibling rows, so clone paths
                // must carry the same explicit marker as the unit-destination clone path.
                var existingBooks = _bookService.GetBooksByAuthorId(author.Id);
                var baseSlug = originalBook.TitleSlug;
                if (string.IsNullOrEmpty(baseSlug))
                {
                    // Keep normalization consistent with AuthorImportService.cs:725
                    baseSlug = originalBook.Title?.ToLowerInvariant().Replace(" ", "-") ?? $"book-{originalBook.Id}";
                    _logger.Warn("Book {0} (ID: {1}) has null or empty TitleSlug, generated: {2}", 
                                originalBook.Title, originalBook.Id, baseSlug);
                }
                
                // Strip existing numeric suffix if present (avoids foo_2_3 problem)
                var lastUnderscore = baseSlug.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    var suffix = baseSlug.Substring(lastUnderscore + 1);
                    if (int.TryParse(suffix, out _))
                    {
                        baseSlug = baseSlug.Substring(0, lastUnderscore);
                    }
                }

                var existingSlugs = new HashSet<string>(
                    existingBooks
                        .Where(b => !string.IsNullOrWhiteSpace(b.TitleSlug))
                        .Select(b => b.TitleSlug),
                    StringComparer.Ordinal);

                var copySlugBase = $"{baseSlug}_copy_{copySuffix}";
                if (!string.IsNullOrWhiteSpace(narratorIdentifier))
                {
                    copySlugBase = $"{copySlugBase}_{narratorIdentifier}";
                }

                int counter = 1;
                string nextSlug;
                do
                {
                    nextSlug = counter == 1 ? copySlugBase : $"{copySlugBase}_{counter}";
                    counter++;
                } while (existingSlugs.Contains(nextSlug));

                // Create the duplicate book
                var duplicateBook = new Book
                {
                    TitleSlug = nextSlug,
                    Title = originalBook.Title,
                    CleanTitle = originalBook.CleanTitle,
                    Overview = originalBook.Overview,
                    AuthorId = author.Id,
                    Author = author,
                    AudiobookMonitored = localBook.Quality.Quality.Id > 4,
                    EbookMonitored = localBook.Quality.Quality.Id <= 4,
                    AnyEditionOk = anyEditionOk,
                    Added = DateTime.UtcNow,
                    ReleaseDate = originalBook.ReleaseDate,
                    Links = originalBook.Links != null ? new List<Links>(originalBook.Links) : new List<Links>(),
                    Genres = originalBook.Genres != null ? new List<string>(originalBook.Genres) : new List<string>(),
                    Ratings = originalBook.Ratings,
                    MediaType = mediaType,
                    UnitKeyHash = unitKeyHash,
                    HardcoverBookId = originalBook.HardcoverBookId,
                    RemoteProviderIds = CloneRemoteProviderIds(originalBook.RemoteProviderIds),
                    IsGraphicAudio = DetectGraphicAudioFromLocalTags(localBook),
                    
                    // Copy all metadata fields that were previously missing
                    Images = originalBook.Images != null ? new List<MediaCover.MediaCover>(originalBook.Images) : new List<MediaCover.MediaCover>(),
                    OpenLibraryWorkId = originalBook.OpenLibraryWorkId,
                    ProviderUrls = originalBook.ProviderUrls != null ? new ProviderUrlMap(originalBook.ProviderUrls) : new ProviderUrlMap(),
                    LastInfoSync = originalBook.LastInfoSync,
                    LastUpdated = DateTime.UtcNow,
                    PageCount = originalBook.PageCount,
                    Publisher = originalBook.Publisher,
                    PublicationYear = originalBook.PublicationYear,
                    LanguageCode = originalBook.LanguageCode,
                    LanguageName = originalBook.LanguageName,
                    SeriesId = originalBook.SeriesId,
                    SeriesName = originalBook.SeriesName,
                    SeriesPosition = originalBook.SeriesPosition,
                    DurationMinutes = originalBook.DurationMinutes,
                    AudioProductionType = originalBook.AudioProductionType,
                    Narrator = originalBook.Narrator,
                    GoodreadsWorkId = originalBook.GoodreadsWorkId
                };

                // Ensure unique slug
                _bookService.InsertMany(new List<Book> { duplicateBook });

                // Create edition for the new book
                var newEdition = new Edition
                {
                    Title = originalEdition.Title,
                    TitleSlug = $"{duplicateBook.TitleSlug}_edition",
                    ForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(originalEdition),
                    Monitored = true,
                    ManualAdd = false,
                    BookId = duplicateBook.Id,
                    Book = duplicateBook,
                    Ratings = originalEdition.Ratings,
                    ReviewCount = originalEdition.ReviewCount,
                    ReleaseDate = originalEdition.ReleaseDate,
                    PageCount = originalEdition.PageCount,
                    Isbn13 = originalEdition.Isbn13,
                    Asin = originalEdition.Asin,
                    Asins = originalEdition.Asins != null ? new List<string>(originalEdition.Asins) : new List<string>(),
                    GoodreadsEditionId = originalEdition.GoodreadsEditionId,
                    HardcoverEditionId = originalEdition.HardcoverEditionId,
                    Format = originalEdition.Format,
                    IsEbook = originalEdition.IsEbook,
                    ReadingFormatId = originalEdition.ReadingFormatId,
                    Narrator = originalEdition.Narrator,
                    NarratorNames = originalEdition.NarratorNames != null ? new List<string>(originalEdition.NarratorNames) : new List<string>(),
                    DurationSeconds = originalEdition.DurationSeconds,
                    ChapterCount = originalEdition.ChapterCount,
                    HasChapters = originalEdition.HasChapters,
                    Chapters = originalEdition.Chapters?.Select(c => new EditionChapter
                    {
                        Title = c?.Title,
                        StartOffsetMs = c?.StartOffsetMs ?? 0,
                        StartOffsetSec = c?.StartOffsetSec ?? 0,
                        LengthMs = c?.LengthMs ?? 0
                    }).ToList() ?? new List<EditionChapter>(),
                    AudibleASIN = originalEdition.AudibleASIN,
                    AudioProductionType = originalEdition.AudioProductionType
                };

                _editionService.InsertMany(new List<Edition> { newEdition });
                
                // Trigger series link creation for the new book
                _eventAggregator.PublishEvent(new BookAddedEvent(duplicateBook, false));

                return new BatchBookResult { NewBook = duplicateBook, NewEdition = newEdition };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create new book instance for batch");
                return null;
            }
        }

        private static HashSet<string> CloneRemoteProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => id.IsNotNullOrWhiteSpace())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        private string GetAdditionalCopyPathCollisionReason(List<ImportDecision<LocalBook>> bookDecisions, Book book, Author author)
        {
            var plannedDestinations = new List<string>();

            foreach (var decision in bookDecisions)
            {
                var localBook = decision?.Item;
                if (localBook == null || localBook.Edition == null || localBook.Quality == null)
                {
                    continue;
                }

                localBook.Author ??= author;
                localBook.Book ??= book;
                localBook.Edition.Book ??= book;

                var previewBookFile = new BookFile
                {
                    Path = localBook.Path.CleanFilePath(),
                    Quality = localBook.Quality,
                    EditionId = localBook.Edition.Id,
                    Edition = localBook.Edition,
                    Author = author,
                    Part = localBook.Part,
                    PartCount = localBook.PartCount,
                    MediaType = BookFile.DetermineMediaType(localBook.Quality)
                };

                var destinationPath = _bookFileMover.GetImportDestinationPath(previewBookFile, localBook);
                if (destinationPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (plannedDestinations.Any(existing => existing.PathEquals(destinationPath)))
                {
                    return $"Additional physical copy cannot be imported because multiple files would resolve to the same managed destination: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }

                plannedDestinations.Add(destinationPath);

                var existingTracked = _mediaFileService.GetFileWithPath(destinationPath);
                var destinationExists = DestinationFileExists(destinationPath);
                if (existingTracked != null && destinationExists && existingTracked.Path.PathNotEquals(localBook.Path))
                {
                    return $"Additional physical copy cannot be imported because the managed destination is already occupied: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }

                if (destinationExists && (existingTracked == null || existingTracked.Path.PathNotEquals(localBook.Path)))
                {
                    return $"Additional physical copy cannot be imported because the managed destination already exists on disk: {destinationPath}. Current naming settings cannot represent a separate copy.";
                }
            }

            return null;
        }
    }
}
