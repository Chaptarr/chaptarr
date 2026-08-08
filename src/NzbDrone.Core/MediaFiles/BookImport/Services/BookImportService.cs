using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport.Aggregation;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Authors;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.MediaFiles.BookImport.Services
{
    public class BookImportService : IBookImportService
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IUpgradeMediaFiles _upgradeMediaFiles;
        private readonly NzbDrone.Core.Configuration.IConfigService _configService;
        private readonly IAugmentingService _augmentingService;
        private readonly IDiskProvider _diskProvider;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly IEventAggregator _eventAggregator;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public BookImportService(
            IBookService bookService,
            IEditionService editionService,
            IMediaFileService mediaFileService,
            IUpgradeMediaFiles upgradeMediaFiles,
            IAugmentingService augmentingService,
            IDiskProvider diskProvider,
            IMediaInfoExtractor mediaInfoExtractor,
            IEventAggregator eventAggregator,
            NzbDrone.Core.Configuration.IConfigService configService,
            IAuthorService authorService,
            Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _upgradeMediaFiles = upgradeMediaFiles;
            _augmentingService = augmentingService;
            _diskProvider = diskProvider;
            _mediaInfoExtractor = mediaInfoExtractor;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _authorService = authorService;
            _logger = logger;
        }

        public Task ImportFileAsync(string path, int bookId, string quality)
        {
            // Backward-compatible overload: no tags provided
            return ImportFileAsync(path, bookId, quality, null);
        }

        public Task ImportFileAsync(string path, int bookId, string quality, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> tags)
        {
            return ImportFileAsync(path, bookId, null, quality, tags);
        }

        public Task ImportFileAsync(string path, int bookId, int? editionId, string quality, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> tags)
        {
            try
            {
                // Verify file exists
                if (!_diskProvider.FileExists(path))
                {
                    _logger.Error("File does not exist: {0}", path);
                    return Task.CompletedTask;
                }

                // Get the book
                var book = _bookService.GetBook(bookId);
                if (book == null)
                {
                    _logger.Error("Book not found: {0}", bookId);
                    return Task.CompletedTask;
                }

                var allEditions = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();
                var requestedEdition = editionId.HasValue && editionId.Value > 0
                    ? _editionService.GetEdition(editionId.Value)
                    : null;

                if (requestedEdition != null && requestedEdition.BookId != book.Id)
                {
                    _logger.Warn("[IMPORT] Edition {0} is not owned by BookId={1}; refusing legacy apply", requestedEdition.Id, book.Id);
                    return Task.CompletedTask;
                }

                if (requestedEdition != null)
                {
                    var conflictingProtectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(book, allEditions, requestedEdition.Id);
                    if (conflictingProtectedEdition != null)
                    {
                        _logger.Warn("[EDITION-PIN-PROTECTION] Refusing legacy apply of EditionId={0} to pinned BookId={1}; protected EditionId={2}",
                            requestedEdition.Id,
                            book.Id,
                            conflictingProtectedEdition.Id);
                        return Task.CompletedTask;
                    }
                }

                // Create LocalBook for import
                var localBook = new LocalBook
                {
                    Path = path,
                    Book = book,
                    Author = book.Author,
                    Size = _diskProvider.GetFileSize(path),
                    Modified = _diskProvider.FileGetLastWrite(path),
                    Quality = ParseQuality(quality)
                };

                // Populate RawTags when available (from staging), so augmenting/parsing has inputs
                if (tags != null && tags.Count > 0)
                {
                    localBook.RawTags = new RawFileTags { AllTags = tags };
                }

                // Augment metadata (format, quality, etc.)
                _augmentingService.Augment(localBook, false);

                // Ensure we have an edition
                if (requestedEdition != null)
                {
                    localBook.Edition = requestedEdition;
                }

                if (localBook.Edition == null)
                {
                    // Use preferred edition or monitored edition
                    localBook.Edition = allEditions.FirstOrDefault(e => e.Monitored) ?? allEditions.FirstOrDefault();
                    
                    if (localBook.Edition == null)
                    {
                        _logger.Error("No edition found for book: {0}", book.Title);
                        return Task.CompletedTask;
                    }
                }

                if (localBook.Edition.BookId != book.Id)
                {
                    _logger.Warn("[IMPORT] Edition {0} is not owned by BookId={1}; refusing legacy apply", localBook.Edition.Id, book.Id);
                    return Task.CompletedTask;
                }

                // Check if file already exists in library (global path-level idempotency)
                var existingFile = _mediaFileService.GetFileWithPath(path);
                
                if (existingFile != null)
                {
                    _logger.Debug("[IMPORT] File already exists in library (global path match): {0}", path);
                    return Task.CompletedTask;
                }

                // Create BookFile entity
                var bookFile = new BookFile
                {
                    Path = path,
                    Size = localBook.Size,
                    Modified = localBook.Modified,
                    DateAdded = DateTime.UtcNow,
                    // Prefer extension-based quality detection; fall back to parsed quality
                    Quality = DetermineQualityByExtension(path) ?? localBook.Quality ?? new QualityModel { Quality = Quality.Unknown },
                    // Ensure non-null MediaInfo to satisfy DB constraint; extractor can enrich later
                    MediaInfo = new MediaInfoModel(),
                    EditionId = localBook.Edition.Id,
                    Part = localBook.Part,
                    MediaType = book.MediaType == 0 ? "audiobook" : "ebook",
                    AllTags = tags,
                    DurationSeconds = GetDurationSeconds(path)
                };

                // Get the author's folder (books are stored under author path)
                // Use media-type-specific path: audiobooks go to AudiobookPath, ebooks to EbookPath
                var bookFolder = book.MediaType == 0
                    ? book.Author?.AudiobookPath
                    : book.Author?.EbookPath;
                
                // Use UpgradeMediaFiles to respect Media Management (rename + move/copy/hardlink)
                // Determine copyOnly based on context:
                // - If the file is already under the author's folder, moving is safe (copyOnly=false)
                // - Otherwise default to copyOnly=true to preserve source (download folder/manual import)
                bool copyOnly = true;
                try
                {
                    var authorFolder = book.Author?.Path;
                    if (!string.IsNullOrWhiteSpace(authorFolder) && path.StartsWith(authorFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        copyOnly = false;
                    }
                }
                catch { /* default to copyOnly=true */ }
                var moveResult = _upgradeMediaFiles.UpgradeBookFile(bookFile, localBook, copyOnly);
                if (moveResult?.BookFile != null)
                {
                    bookFile = moveResult.BookFile;
                }

                // Add the file to database
                _mediaFileService.Add(bookFile);

                // Ensure the matched edition is monitored so UI/APIs resolve the correct title
                try
                {
                    if (localBook.Edition != null)
                    {
                        _editionService.SetMonitored(localBook.Edition, false); // automatic selection during import
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[IMPORT] Failed to set monitored edition for bookId={0}", bookId);
                }

                // Update book statistics
                // Update book statistics are handled by event handlers

                // Publish import event
                _eventAggregator.PublishEvent(new BookFileAddedEvent(bookFile));

                _logger.Info("Successfully imported file: {0} for book: {1}", path, book.Title);
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to import file: {0} for book: {1}", path, bookId);
                throw;
            }
        }

        // Import a file that already resides within the user's library (root-folder scan)
        // Do not move/rename; only add DB record and emit events, aligning with Readarr behavior.
        public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> tags)
        {
            return ImportExistingFileAsync(path, bookId, editionId, quality, tags, durationSeconds: null, provenance: null);
        }

        public Task<BookImportFileResult> ImportExistingFileAsync(string path, int bookId, int? editionId, string quality, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> tags, int? durationSeconds)
        {
            return ImportExistingFileAsync(path, bookId, editionId, quality, tags, durationSeconds, provenance: null);
        }

        public Task<BookImportFileResult> ImportExistingFileAsync(
            string path,
            int bookId,
            int? editionId,
            string quality,
            Dictionary<string, List<string>> tags,
            int? durationSeconds,
            MatchProvenance provenance)
        {
            return ImportExistingFileAsync(
                new DiscoveredFileWithMetadata
                {
                    Path = path,
                    AllTags = tags,
                    DurationSeconds = durationSeconds
                },
                bookId,
                editionId,
                quality,
                provenance);
        }

        public Task<BookImportFileResult> ImportExistingFileAsync(
            DiscoveredFileWithMetadata file,
            int bookId,
            int? editionId,
            string quality,
            MatchProvenance provenance)
        {
            var path = file?.Path;
            var tags = file?.AllTags;
            var durationSeconds = file?.DurationSeconds;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return Task.FromResult(BookImportFileResult.Failed(path, "INVALID_PATH_AT_APPLY"));
                }

                if (!_diskProvider.FileExists(path))
                {
                    _logger.Error("[SCAN] File does not exist: {0}", path);
                    return Task.FromResult(BookImportFileResult.Failed(path, "FILE_MISSING_AT_APPLY"));
                }

                var book = _bookService.GetBook(bookId);
                if (book == null)
                {
                    _logger.Error("[SCAN] Book not found: {0}", bookId);
                    return Task.FromResult(BookImportFileResult.Unmapped(path, "BOOK_MISSING_AT_APPLY"));
                }

                var allEditions = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();

                // Determine edition
                var targetEdition = editionId.HasValue && editionId.Value > 0
                    ? _editionService.GetEdition(editionId.Value)
                    : allEditions.FirstOrDefault(e => e.Monitored)
                      ?? allEditions.FirstOrDefault();

                if (targetEdition == null)
                {
                    _logger.Error("[SCAN] No edition found for book: {0}", book.Title);
                    return Task.FromResult(BookImportFileResult.Unmapped(path, "EDITION_MISSING_AT_APPLY"));
                }

                if (targetEdition.BookId != book.Id)
                {
                    _logger.Error("[SCAN] Edition {0} belongs to BookId={1}, not requested BookId={2}", targetEdition.Id, targetEdition.BookId, book.Id);
                    return Task.FromResult(BookImportFileResult.Unmapped(path, "EDITION_BOOK_MISMATCH_AT_APPLY"));
                }

                var protectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(book, allEditions, targetEdition.Id);
                if (protectedEdition != null)
                {
                    _logger.Warn("[EDITION-PIN-PROTECTION] Refusing apply of EditionId={0} to pinned BookId={1}; protected EditionId={2}: {3}",
                        targetEdition.Id,
                        book.Id,
                        protectedEdition.Id,
                        path);
                    return Task.FromResult(BookImportFileResult.Unmapped(path, "PINNED_EDITION_DESTINATION_CONFLICT"));
                }

                // If already present in DB, allow remapping previously-unmapped records (EditionId=0).
	                var existing = _mediaFileService.GetFileWithPath(path);
	                if (existing != null)
	                {
	                    if (existing.EditionId == 0)
	                    {
	                        existing.EditionId = targetEdition.Id;
	                        existing.Edition = targetEdition;
	                        if (targetEdition.Book == null)
	                        {
	                            targetEdition.Book = book;
	                        }

	                        existing.Author = book.Author;
	                        RefreshTrackedFileMetadata(existing, file, book);
                        ApplySuccessfulMatchState(existing, provenance, book.Author, book, targetEdition);

	                        _mediaFileService.Update(existing);

	                        var applied = VerifyAppliedFile(path, targetEdition.Id, null, null, book.Author, book, targetEdition);
                        if (!applied.IsApplied)
                        {
                            return Task.FromResult(applied);
                        }

                        // Prefer the edition that just received a file
                        try
                        {
                            _editionService.SetMonitored(targetEdition, false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "[SCAN] Failed to set monitored edition for bookId={0}", bookId);
                        }

                        _eventAggregator.PublishEvent(new BookFileAddedEvent(existing));
                        _logger.Debug("[SCAN] Remapped previously-unmapped file without moving: {0}", path);
                        return Task.FromResult(applied);
                    }

                    if (existing.EditionId == targetEdition.Id)
                    {
                        var fileChanged = MediaFileFreshness.HasChanged(existing, GetObservedSize(file), GetObservedModified(file));
                        var needsRefresh = fileChanged ||
                                           !string.Equals(existing.MediaType, GetMediaType(book), StringComparison.OrdinalIgnoreCase) ||
                                           (file?.Quality != null && existing.Quality != file.Quality);

                        if (!needsRefresh)
                        {
                            _logger.Debug("[SCAN] File already linked and unchanged: {0}", path);
                            return Task.FromResult(BookImportFileResult.AlreadyLinked(path, existing.Id));
                        }

                        RefreshTrackedFileMetadata(existing, file, book, refreshFileEvidence: fileChanged);
                        ApplySuccessfulMatchState(existing, provenance, book.Author, book, targetEdition);
                        _mediaFileService.Update(existing);
                        PublishBookFileUpdated(existing);
                        _logger.Debug("[SCAN] Refreshed metadata for already-linked file: {0}", path);
                        return Task.FromResult(BookImportFileResult.AlreadyLinked(path, existing.Id));
                    }

                    _logger.Warn("[SCAN] File already linked to a different edition (current={0}, requested={1}): {2}",
                        existing.EditionId,
                        targetEdition.Id,
                        path);
                    return Task.FromResult(BookImportFileResult.Unmapped(path, "PATH_LINKED_TO_DIFFERENT_EDITION"));
                }

                // Create BookFile without moving
                var detectedQuality = file?.Quality ?? DetermineQualityByExtension(path) ?? new QualityModel { Quality = Quality.Unknown };
                var detectedDuration = durationSeconds ?? GetDurationSeconds(path);
                var bf = new BookFile
                {
                    Path = path,
                    Size = GetObservedSize(file),
                    Modified = GetObservedModified(file),
                    DateAdded = DateTime.UtcNow,
                    Quality = file?.Quality ?? detectedQuality,
                    MediaInfo = MediaDuration.CreateMediaInfo(detectedDuration),
                    EditionId = targetEdition.Id,
                    MediaType = book.MediaType == 0 ? "audiobook" : "ebook",
                    AllTags = tags,
                    DurationSeconds = detectedDuration
                };

                _mediaFileService.Add(bf);

                var added = VerifyAppliedFile(path, targetEdition.Id, null, provenance, book.Author, book, targetEdition);
                if (!added.IsApplied)
                {
                    return Task.FromResult(added);
                }

                // Prefer the edition that just received a file
                try
                {
                    _editionService.SetMonitored(targetEdition, false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[SCAN] Failed to set monitored edition for bookId={0}", bookId);
                }

                _eventAggregator.PublishEvent(new BookFileAddedEvent(bf));
                _logger.Debug("[SCAN] Tracked existing file without moving: {0}", path);
                return Task.FromResult(added);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SCAN] Failed to track existing file: {0} for book: {1}", path, bookId);
                return Task.FromResult(BookImportFileResult.Failed(path, "APPLY_EXCEPTION"));
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // Batch inventory apply. These files already live in a library root; no disk mutation or
        // transfer is permitted here. Manual/download organizing remains owned by ImportApprovedBooks.
        // ═══════════════════════════════════════════════════════════════════════════════════════
        public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files, int bookId)
        {
            var discovered = files?
                .Select(file => (
                    new DiscoveredFileWithMetadata
                    {
                        Path = file.Path,
                        AllTags = file.Tags,
                        DurationSeconds = file.DurationSeconds
                    },
                    file.EditionId,
                    (MatchProvenance)null))
                .ToList();
            return ImportFilesAsync(discovered, bookId);
        }

        public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(
            List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)> files,
            int bookId)
        {
            var discovered = files?
                .Select(file => (
                    new DiscoveredFileWithMetadata
                    {
                        Path = file.Path,
                        AllTags = file.Tags,
                        DurationSeconds = file.DurationSeconds
                    },
                    file.EditionId,
                    file.Provenance))
                .ToList();
            return ImportFilesAsync(discovered, bookId);
        }

        public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(
            List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files,
            int bookId)
        {
            try
            {
                if (files == null || files.Count == 0)
                {
                    return Task.FromResult<IReadOnlyList<BookImportFileResult>>(Array.Empty<BookImportFileResult>());
                }

                var book = _bookService.GetBook(bookId);
                if (book == null)
                {
                    _logger.Warn("[IMPORT] Book not found: {0}", bookId);
                    return Task.FromResult(CreateResultsForPaths(files.Select(file => file.File?.Path), ImportOutcome.Unmapped, "BOOK_MISSING_AT_APPLY"));
                }

                // Ensure Author is loaded (critical for both paths)
                if (book.AuthorId > 0 && (book.Author == null || string.IsNullOrWhiteSpace(book.Author.Path)))
                {
                    book.Author = _authorService.GetAuthor(book.AuthorId);
                }
                if (book.Author == null || book.Author.Id <= 0 || string.IsNullOrWhiteSpace(book.Author.Path))
                {
                    _logger.Error("[IMPORT] Cannot import - book has no author: {0} (bookId={1})", book.Title, bookId);
                    return Task.FromResult(CreateResultsForPaths(files.Select(file => file.File?.Path), ImportOutcome.Unmapped, "AUTHOR_MISSING_AT_APPLY"));
                }

                // Get all editions for this book once
                var allEditions = _editionService.GetEditionsByBook(book.Id);
                var defaultEdition = allEditions.FirstOrDefault(e => e.Monitored) ?? allEditions.FirstOrDefault();

                if (defaultEdition == null)
                {
                    _logger.Error("[IMPORT] No editions for book: {0} (bookId={1})", book.Title, bookId);
                    return Task.FromResult(CreateResultsForPaths(files.Select(file => file.File?.Path), ImportOutcome.Unmapped, "EDITION_MISSING_AT_APPLY"));
                }

                return TrackFilesInPlace(files, book, allEditions, defaultEdition);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[IMPORT] Failed to track files in place for bookId={0}", bookId);
                return Task.FromResult(CreateResultsForPaths(files?.Select(file => file.File?.Path), ImportOutcome.Failed, "APPLY_EXCEPTION"));
            }
        }

        /// <summary>
        /// TrackInPlace: inventory executor. It may verify existence, but never mutates or transfers files.
        /// Current scan/retry callers supply size/mtime so apply does not re-stat unchanged media.
        /// - No copy/move/rename/delete
        /// - No tag writing
        /// - Updates existing DB rows (especially unmapped EditionId=0)
        /// </summary>
        private Task<IReadOnlyList<BookImportFileResult>> TrackFilesInPlace(
            List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)> files,
            Book book,
            List<Edition> allEditions,
            Edition defaultEdition)
        {
            var author = book.Author;
            var results = new List<BookImportFileResult>();
            var requestedFiles = files
                .GroupBy(f => f.File?.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.File?.Path, NaturalSortComparer.Instance)
                .ToList();

            var existingFiles = new List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)>();
            foreach (var file in requestedFiles)
            {
                if (string.IsNullOrWhiteSpace(file.File?.Path))
                {
                    results.Add(BookImportFileResult.Failed(file.File?.Path, "INVALID_PATH_AT_APPLY"));
                    continue;
                }

                try
                {
                    if (_diskProvider.FileExists(file.File.Path))
                    {
                        existingFiles.Add(file);
                    }
                    else
                    {
                        _logger.Debug("[TRACK] File disappeared before apply: {0}", file.File.Path);
                        results.Add(BookImportFileResult.Failed(file.File.Path, "FILE_MISSING_AT_APPLY"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[TRACK] Failed checking file before apply: {0}", file.File.Path);
                    results.Add(BookImportFileResult.Failed(file.File.Path, "FILE_CHECK_FAILED_AT_APPLY"));
                }
            }

            if (existingFiles.Count == 0)
            {
                _logger.Debug("[TRACK] No files exist on disk for book {0}", book.Title);
                return Task.FromResult<IReadOnlyList<BookImportFileResult>>(results);
            }

            var resolvedFiles = new List<((DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance) Request, Edition Edition)>();
            foreach (var file in existingFiles)
            {
                Edition edition;
                if (file.EditionId.HasValue && file.EditionId.Value > 0)
                {
                    edition = allEditions.FirstOrDefault(candidate => candidate.Id == file.EditionId.Value);
                    if (edition == null)
                    {
                        _logger.Warn("[TRACK] Requested edition {0} is not owned by BookId={1}: {2}", file.EditionId.Value, book.Id, file.File.Path);
                        results.Add(BookImportFileResult.Unmapped(file.File.Path, "EDITION_NOT_FOUND_FOR_BOOK_AT_APPLY"));
                        continue;
                    }
                }
                else
                {
                    edition = defaultEdition;
                }

                var protectedEdition = EditionPinPolicy.FindConflictingProtectedEdition(book, allEditions, edition.Id);
                if (protectedEdition != null)
                {
                    _logger.Warn("[EDITION-PIN-PROTECTION] Refusing TrackInPlace apply of EditionId={0} to pinned BookId={1}; protected EditionId={2}: {3}",
                        edition.Id,
                        book.Id,
                        protectedEdition.Id,
                        file.File.Path);
                    results.Add(BookImportFileResult.Unmapped(file.File.Path, "PINNED_EDITION_DESTINATION_CONFLICT"));
                    continue;
                }

                resolvedFiles.Add((file, edition));
            }

            if (resolvedFiles.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<BookImportFileResult>>(results);
            }

            var partAssignments = PartAssignmentHelper.BuildPathAssignmentsByEdition(
                resolvedFiles.Select(item => (item.Request.File.Path, (int?)item.Edition.Id)),
                defaultEdition?.Id ?? 0);

            // Resolve existing DB rows per path (list size is per-book and should be small).
            // Avoid MediaFileRepository.GetFileWithPath(List<>) which materializes the entire table.
            var existingByPath = new Dictionary<string, BookFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in resolvedFiles.Select(item => item.Request.File.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var existing = _mediaFileService.GetFileWithPath(p);
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Path))
                {
                    existingByPath[existing.Path] = existing;
                }
            }

            var newFiles = new List<BookFile>();
            var pendingUpdates = new List<(BookFile File, string Path, int EditionId, int Part, bool RelinkedFromUnmapped, bool IdentityAlreadyLinked, MatchProvenance Provenance, Edition Edition)>();
            var pendingAdds = new List<(BookFile File, string Path, int EditionId, int Part, MatchProvenance Provenance, Edition Edition)>();
            var editionsTouched = new HashSet<int>();
            var relinkedFromUnmapped = new List<BookFile>();

            for (int i = 0; i < resolvedFiles.Count; i++)
            {
                var request = resolvedFiles[i].Request;
                var file = request.File;
                var edition = resolvedFiles[i].Edition;
                var assignment = partAssignments[file.Path];

                // Check if this path already exists in DB
                if (existingByPath.TryGetValue(file.Path, out var existingFile))
                {
                    var previousEditionId = existingFile.EditionId;

                    // Identity is already satisfied. Refresh mutable metadata only when the shared
                    // file-freshness predicate (or deterministic grouping metadata) says it changed.
                    if (existingFile.EditionId == edition.Id && existingFile.EditionId > 0 && existingFile.Part == assignment.Part)
                    {
                        var fileChanged = MediaFileFreshness.HasChanged(existingFile, GetObservedSize(file), GetObservedModified(file));
                        var needsRefresh = fileChanged ||
                                           existingFile.PartCount != assignment.PartCount ||
                                           !string.Equals(existingFile.MediaType, GetMediaType(book), StringComparison.OrdinalIgnoreCase) ||
                                           (file.Quality != null && existingFile.Quality != file.Quality);

                        if (!needsRefresh)
                        {
                            _logger.Debug("[TRACK] Already correctly mapped and unchanged: {0}", file.Path);
                            results.Add(BookImportFileResult.AlreadyLinked(file.Path, existingFile.Id));
                            continue;
                        }

                        _logger.Debug("[TRACK] Refreshing metadata for already-linked file: {0}", file.Path);
                        RefreshTrackedFileMetadata(existingFile, file, book, assignment.PartCount, fileChanged);
                        ApplySuccessfulMatchState(existingFile, request.Provenance, author, book, edition);
                        pendingUpdates.Add((existingFile, file.Path, edition.Id, assignment.Part, false, true, null, edition));
                        continue;
                    }

                    // UPDATE existing row (unmapped EditionId=0, wrong edition, or wrong part)
                    _logger.Debug("[TRACK] Updating existing file EditionId={0}→{1}, Part={2}→{3}: {4}",
                        existingFile.EditionId, edition.Id, existingFile.Part, assignment.Part, file.Path);

                    existingFile.EditionId = edition.Id;
                    existingFile.Edition = edition;
                    if (edition.Book == null)
                    {
                        edition.Book = book;
                    }

                    existingFile.Author = author;
                    existingFile.Part = assignment.Part;
                    RefreshTrackedFileMetadata(existingFile, file, book, assignment.PartCount);
                    ApplySuccessfulMatchState(existingFile, request.Provenance, author, book, edition);

                    pendingUpdates.Add((existingFile, file.Path, edition.Id, assignment.Part, previousEditionId <= 0 && edition.Id > 0, false, null, edition));
                }
                else
                {
                    // NEW file: Create BookFile record (NO DISK OPS)
                    var detectedDuration = file.DurationSeconds ?? GetDurationSeconds(file.Path);
                    var bf = new BookFile
                    {
                        Path = file.Path.CleanFilePath(),
                        Size = GetObservedSize(file),
                        Modified = GetObservedModified(file),
                        DateAdded = DateTime.UtcNow,
                        Quality = file.Quality ?? DetermineQualityByExtension(file.Path) ?? new QualityModel { Quality = Quality.Unknown },
                        MediaInfo = MediaDuration.CreateMediaInfo(detectedDuration),
                        EditionId = edition.Id,
                        Part = assignment.Part,
                        PartCount = assignment.PartCount,
                        MediaType = GetMediaType(book),
                        AllTags = file.AllTags,
                        DurationSeconds = detectedDuration
                    };
                    bf.Edition = edition;
                    if (bf.Edition != null && bf.Edition.Book == null)
                    {
                        bf.Edition.Book = book;
                    }
                    bf.Author = author;
                    newFiles.Add(bf);
                    pendingAdds.Add((bf, file.Path, edition.Id, assignment.Part, request.Provenance, edition));
                }
            }

            foreach (var pending in pendingUpdates)
            {
                try
                {
                    _mediaFileService.Update(pending.File);
                    var applied = VerifyAppliedFile(pending.Path, pending.EditionId, pending.Part, pending.Provenance, author, book, pending.Edition);
                    if (pending.IdentityAlreadyLinked && applied.IsApplied)
                    {
                        applied = BookImportFileResult.AlreadyLinked(pending.Path, applied.BookFileId);
                        PublishBookFileUpdated(pending.File);
                    }
                    results.Add(applied);

                    if (applied.IsApplied)
                    {
                        editionsTouched.Add(pending.EditionId);
                        if (pending.RelinkedFromUnmapped)
                        {
                            relinkedFromUnmapped.Add(pending.File);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[TRACK] Failed updating existing file: {0}", pending.Path);
                    results.Add(BookImportFileResult.Failed(pending.Path, "UPDATE_FAILED_AT_APPLY"));
                }
            }

            if (newFiles.Count > 0)
            {
                Exception addException = null;
                try
                {
                    _mediaFileService.AddMany(newFiles);
                }
                catch (Exception ex)
                {
                    addException = ex;
                    _logger.Warn(ex, "[TRACK] Failed batch-adding {0} files for book {1}; verifying each path", newFiles.Count, book.Title);
                }

                foreach (var pending in pendingAdds)
                {
                    var applied = VerifyAppliedFile(pending.Path, pending.EditionId, pending.Part, pending.Provenance, author, book, pending.Edition);
                    if (!applied.IsApplied && addException != null)
                    {
                        applied = BookImportFileResult.Failed(pending.Path, "ADD_FAILED_AT_APPLY");
                    }

                    results.Add(applied);
                    if (applied.IsApplied)
                    {
                        editionsTouched.Add(pending.EditionId);
                    }
                }
            }

            // For scan/staging: prefer monitoring the single edition that actually received files (if unambiguous).
            if (editionsTouched.Count == 1)
            {
                try
                {
                    var editionId = editionsTouched.First();
                    var monitored = allEditions.FirstOrDefault(e => e.Id == editionId) ?? defaultEdition;
                    _editionService.SetMonitored(monitored, false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[TRACK] Failed to set monitored edition for bookId={0}", book.Id);
                }
            }

            // Publish events for rows that were relinked from unmapped so the UI updates immediately.
            foreach (var bf in relinkedFromUnmapped)
            {
                try
                {
                    _eventAggregator.PublishEvent(new BookFileAddedEvent(bf));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[TRACK] Failed publishing BookFileAddedEvent for relinked file: {0}", bf?.Path);
                }
            }

            _logger.Debug("[TRACK] Tracked {0} files in place for book {1} (new={2}, updated={3})",
                resolvedFiles.Count, book.Title, newFiles.Count, pendingUpdates.Count);

            return Task.FromResult<IReadOnlyList<BookImportFileResult>>(results);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // LEGACY: Old signature - defaults to TrackInPlace for safety (no accidental file movement)
        // ═══════════════════════════════════════════════════════════════════════════════════════
        public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags)> files,
                                                                          int bookId,
                                                                          int? editionId,
                                                                          string quality)
        {
            // Convert to new format with per-file EditionId
            var converted = files
                .Select(f => (f.Path, editionId, f.Tags, (int?)null, (MatchProvenance)null))
                .ToList();

            return ImportFilesAsync(converted, bookId);
        }

        public Task<IReadOnlyList<BookImportFileResult>> ImportFilesAsync(List<(string Path, Dictionary<string, List<string>> Tags, int? DurationSeconds)> files,
                                                                          int bookId,
                                                                          int? editionId,
                                                                          string quality)
        {
            var converted = files
                .Select(f => (f.Path, editionId, f.Tags, f.DurationSeconds, (MatchProvenance)null))
                .ToList();

            return ImportFilesAsync(converted, bookId);
        }

        private long GetObservedSize(DiscoveredFileWithMetadata file)
        {
            if (file != null && file.Modified != default)
            {
                return file.Size;
            }

            return _diskProvider.GetFileSize(file?.Path);
        }

        private DateTime GetObservedModified(DiscoveredFileWithMetadata file)
        {
            if (file != null && file.Modified != default)
            {
                return file.Modified.ToUniversalTime();
            }

            return _diskProvider.FileGetLastWrite(file?.Path).ToUniversalTime();
        }

        private void RefreshTrackedFileMetadata(
            BookFile existing,
            DiscoveredFileWithMetadata observed,
            Book book,
            int? partCount = null,
            bool refreshFileEvidence = true)
        {
            existing.PartCount = partCount ?? existing.PartCount;
            existing.MediaType = GetMediaType(book);

            if (!refreshFileEvidence)
            {
                if (observed?.Quality != null)
                {
                    existing.Quality = observed.Quality;
                }

                return;
            }

            var durationSeconds = observed?.DurationSeconds;
            if (!MediaDuration.HasDuration(durationSeconds))
            {
                durationSeconds = GetDurationSeconds(observed?.Path);
            }

            existing.Quality = observed?.Quality ?? DetermineQualityByExtension(observed?.Path) ?? new QualityModel { Quality = Quality.Unknown };
            existing.Size = GetObservedSize(observed);
            existing.Modified = GetObservedModified(observed);
            existing.AllTags = observed?.AllTags ?? existing.AllTags;
            existing.DurationSeconds = durationSeconds;
            existing.MediaInfo ??= new MediaInfoModel();
            existing.MediaInfo.Duration = MediaDuration.HasDuration(durationSeconds)
                ? TimeSpan.FromSeconds(durationSeconds.Value)
                : TimeSpan.Zero;
        }

        private static string GetMediaType(Book book)
        {
            return book?.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
        }

        private bool ApplySuccessfulMatchState(
            BookFile persisted,
            MatchProvenance provenance,
            Author author,
            Book book,
            Edition edition)
        {
            if (persisted == null)
            {
                return false;
            }

            var shouldUpdate = false;
            if (provenance != null)
            {
                var resolvedAuthor = author ?? book?.Author;
                if (resolvedAuthor == null && book?.AuthorId > 0)
                {
                    resolvedAuthor = _authorService.GetAuthor(book.AuthorId);
                }

                persisted.MatchProvenance = provenance.CloneForDestination(resolvedAuthor, book, edition);
                shouldUpdate = true;
            }

            if (persisted.LastMatchAttempt.HasValue || !string.IsNullOrWhiteSpace(persisted.MatchDetails))
            {
                persisted.LastMatchAttempt = null;
                persisted.MatchDetails = null;
                shouldUpdate = true;
            }

            return shouldUpdate;
        }

        private void PublishBookFileUpdated(BookFile bookFile)
        {
            try
            {
                _eventAggregator.PublishEvent(new BookFileUpdatedEvent(bookFile));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[TRACK] Failed publishing BookFileUpdatedEvent for refreshed file: {0}", bookFile?.Path);
            }
        }

        private void PersistSuccessfulMatch(
            BookFile persisted,
            MatchProvenance provenance,
            Author author,
            Book book,
            Edition edition)
        {
            try
            {
                if (persisted == null || persisted.Id <= 0)
                {
                    _logger.Warn("[MATCH-PROVENANCE] Applied file could not be reloaded for provenance");
                    return;
                }

                if (ApplySuccessfulMatchState(persisted, provenance, author, book, edition))
                {
                    _mediaFileService.Update(persisted);
                }
            }
            catch (Exception ex)
            {
                // Matching/import success remains truthful even if optional explanation persistence fails.
                _logger.Warn(ex, "[MATCH-PROVENANCE] Failed persisting successful match explanation for {0}", persisted?.Path);
            }
        }

        private BookImportFileResult VerifyAppliedFile(
            string path,
            int expectedEditionId,
            int? expectedPart,
            MatchProvenance provenance = null,
            Author author = null,
            Book book = null,
            Edition edition = null)
        {
            var persisted = _mediaFileService.GetFileWithPath(path);
            if (persisted == null)
            {
                _logger.Error("[APPLY-VERIFY] No BookFile row found after apply: {0}", path);
                return BookImportFileResult.Failed(path, "APPLY_POSTCONDITION_MISSING_ROW");
            }

            if (persisted.Id <= 0)
            {
                _logger.Error("[APPLY-VERIFY] BookFile row has no persisted ID after apply: {0}", path);
                return BookImportFileResult.Failed(path, "APPLY_POSTCONDITION_INVALID_ROW_ID");
            }

            if (persisted.EditionId != expectedEditionId)
            {
                _logger.Error("[APPLY-VERIFY] Edition mismatch after apply for {0}: expected={1}, actual={2}", path, expectedEditionId, persisted.EditionId);
                return BookImportFileResult.Failed(path, "APPLY_POSTCONDITION_EDITION_MISMATCH");
            }

            if (expectedPart.HasValue && persisted.Part != expectedPart.Value)
            {
                _logger.Error("[APPLY-VERIFY] Part mismatch after apply for {0}: expected={1}, actual={2}", path, expectedPart.Value, persisted.Part);
                return BookImportFileResult.Failed(path, "APPLY_POSTCONDITION_PART_MISMATCH");
            }

            PersistSuccessfulMatch(persisted, provenance, author, book, edition);

            return BookImportFileResult.Imported(path, persisted.Id);
        }

        private static IReadOnlyList<BookImportFileResult> CreateResultsForPaths(IEnumerable<string> paths, ImportOutcome outcome, string reasonCode)
        {
            var results = new List<BookImportFileResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                var key = path ?? string.Empty;
                if (!seen.Add(key))
                {
                    continue;
                }

                results.Add(outcome == ImportOutcome.Unmapped
                    ? BookImportFileResult.Unmapped(path, reasonCode)
                    : BookImportFileResult.Failed(path, reasonCode));
            }

            return results;
        }

        private QualityModel ParseQuality(string quality)
        {
            // Parse quality string if provided, otherwise use Unknown
            if (string.IsNullOrWhiteSpace(quality))
            {
                return new QualityModel { Quality = Quality.Unknown };
            }

            // Try to parse from string representation
            // This is simplified - in reality you'd use QualityParser
            return new QualityModel { Quality = Quality.Unknown };
        }

        private QualityModel DetermineQualityByExtension(string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(ext)) return null;

                var mediaInfo = MediaFileExtensions.IsMatroskaAudioExtension(ext)
                    ? _mediaInfoExtractor.ExtractMediaInfo(filePath)
                    : null;
                return new QualityModel { Quality = MediaFileExtensions.GetQualityForExtension(ext, mediaInfo) };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[IMPORT] Failed to determine quality by extension for '{0}'", filePath);
                return null;
            }
        }
        private int? GetDurationSeconds(string filePath)
        {
            return MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(filePath));
        }
    }
}
